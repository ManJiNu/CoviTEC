using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ConviTEC.Messaging;

/// <summary>
/// Bus sobre RabbitMQ (RabbitMQ.Client 7.x, API asíncrona).
/// Un exchange "direct" compartido; cada sede tiene su cola "sede.{codigo}" enlazada con su código como routing key.
/// </summary>
public sealed class RabbitMqBus : IBusMensajes
{
    private const string Exchange = "convitec.sedes";

    private readonly OpcionesMensajeria _opciones;
    private readonly SemaphoreSlim _publicacion = new(1, 1); // un IChannel no admite publicaciones concurrentes
    private IConnection? _conexion;
    private IChannel? _canal;

    public RabbitMqBus(OpcionesMensajeria opciones)
    {
        _opciones = opciones;
    }

    public string SedeLocal => _opciones.SedeLocal;
    public string[] OtrasSedes => _opciones.OtrasSedes();

    public async Task IniciarAsync(Func<Mensaje, Task> manejador, CancellationToken ct = default)
    {
        var fabrica = new ConnectionFactory
        {
            HostName = _opciones.Host,
            Port = _opciones.Puerto,
            UserName = _opciones.Usuario,
            Password = _opciones.Contrasena,
            VirtualHost = _opciones.VirtualHost,
            AutomaticRecoveryEnabled = true
        };

        _conexion = await fabrica.CreateConnectionAsync(ct);
        IChannel canal = await _conexion.CreateChannelAsync(cancellationToken: ct);
        _canal = canal;

        await canal.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        // Se declaran las colas de todas las sedes para que los mensajes no se pierdan
        // si una sede aún no ha arrancado.
        foreach (string sede in _opciones.Sedes)
        {
            string cola = NombreCola(sede);
            await canal.QueueDeclareAsync(cola, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            await canal.QueueBindAsync(cola, Exchange, sede, cancellationToken: ct);
        }

        var consumidor = new AsyncEventingBasicConsumer(canal);
        consumidor.ReceivedAsync += async (_, entrega) =>
        {
            try
            {
                string texto = Encoding.UTF8.GetString(entrega.Body.ToArray());
                Mensaje? mensaje = JsonSerializer.Deserialize<Mensaje>(texto);
                if (mensaje != null) await manejador(mensaje);
            }
            catch (Exception ex)
            {
                // Mensaje mal formado o error de procesamiento: se registra y se descarta sin tumbar la sede.
                Console.Error.WriteLine($"[{SedeLocal}] Mensaje descartado: {ex.Message}");
            }
            await canal.BasicAckAsync(entrega.DeliveryTag, false);
        };

        await canal.BasicConsumeAsync(NombreCola(SedeLocal), autoAck: false, consumer: consumidor, cancellationToken: ct);
    }

    public async Task EnviarAsync(Mensaje mensaje, CancellationToken ct = default)
    {
        IChannel canal = _canal ?? throw new InvalidOperationException("El bus no ha sido iniciado.");
        byte[] cuerpo = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mensaje));

        await _publicacion.WaitAsync(ct);
        try
        {
            await canal.BasicPublishAsync(
                exchange: Exchange,
                routingKey: mensaje.Destino,
                mandatory: false,
                basicProperties: new BasicProperties { Persistent = true },
                body: cuerpo,
                cancellationToken: ct);
        }
        finally
        {
            _publicacion.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_canal != null) await _canal.CloseAsync();
        if (_conexion != null) await _conexion.CloseAsync();
        _publicacion.Dispose();
    }

    private static string NombreCola(string sede) => "sede." + sede;
}
