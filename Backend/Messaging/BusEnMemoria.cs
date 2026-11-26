namespace ConviTEC.Messaging;

/// <summary>Red simulada en memoria para pruebas y desarrollo sin RabbitMQ.</summary>
public sealed class RedEnMemoria
{
    private BusEnMemoria[] _buses = Array.Empty<BusEnMemoria>();

    public BusEnMemoria CrearBus(string sedeLocal, string[] todasLasSedes)
    {
        var bus = new BusEnMemoria(this, sedeLocal, todasLasSedes);
        Array.Resize(ref _buses, _buses.Length + 1);
        _buses[^1] = bus;
        return bus;
    }

    internal Task EntregarAsync(Mensaje mensaje)
    {
        foreach (BusEnMemoria bus in _buses)
        {
            if (bus.SedeLocal == mensaje.Destino) return bus.RecibirAsync(mensaje);
        }
        throw new InvalidOperationException($"Sede desconocida: {mensaje.Destino}");
    }
}

public sealed class BusEnMemoria : IBusMensajes
{
    private readonly RedEnMemoria _red;
    private readonly string[] _todas;
    private Func<Mensaje, Task>? _manejador;

    internal BusEnMemoria(RedEnMemoria red, string sedeLocal, string[] todas)
    {
        _red = red;
        SedeLocal = sedeLocal;
        _todas = todas;
    }

    public string SedeLocal { get; }

    /// <summary>Si es true, los mensajes dirigidos a esta sede se pierden (simula una sede caída).</summary>
    public bool Caida { get; set; }

    public string[] OtrasSedes
    {
        get
        {
            string[] resultado = Array.Empty<string>();
            foreach (string s in _todas)
            {
                if (s == SedeLocal) continue;
                Array.Resize(ref resultado, resultado.Length + 1);
                resultado[^1] = s;
            }
            return resultado;
        }
    }

    public Task IniciarAsync(Func<Mensaje, Task> manejador, CancellationToken ct = default)
    {
        _manejador = manejador;
        return Task.CompletedTask;
    }

    public Task EnviarAsync(Mensaje mensaje, CancellationToken ct = default) => _red.EntregarAsync(mensaje);

    internal Task RecibirAsync(Mensaje mensaje)
    {
        if (Caida || _manejador == null) return Task.CompletedTask;
        return _manejador(mensaje);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
