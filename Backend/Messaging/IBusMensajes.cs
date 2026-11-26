namespace ConviTEC.Messaging;

/// <summary>Canal de mensajes de una sede. Implementaciones: RabbitMqBus (real) y BusEnMemoria (pruebas).</summary>
public interface IBusMensajes : IAsyncDisposable
{
    string SedeLocal { get; }

    /// <summary>Todas las sedes menos la local.</summary>
    string[] OtrasSedes { get; }

    /// <summary>Se conecta y empieza a entregar los mensajes recibidos al manejador.</summary>
    Task IniciarAsync(Func<Mensaje, Task> manejador, CancellationToken ct = default);

    /// <summary>Envía el mensaje a la sede indicada en Mensaje.Destino.</summary>
    Task EnviarAsync(Mensaje mensaje, CancellationToken ct = default);
}
