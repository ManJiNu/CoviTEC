namespace ConviTEC.Messaging;

/// <summary>Configuración de mensajería de una sede (sección "Mensajeria" de appsettings).</summary>
public sealed class OpcionesMensajeria
{
    public string Host { get; set; } = "localhost";
    public int Puerto { get; set; } = 5672;
    public string Usuario { get; set; } = "guest";
    public string Contrasena { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    /// <summary>Código de esta sede: Liberia, Alajuela, SanJose o Limon.</summary>
    public string SedeLocal { get; set; } = "";

    /// <summary>Códigos de todas las sedes (incluida esta).</summary>
    public string[] Sedes { get; set; } = Array.Empty<string>();

    public int TiempoEsperaReservaSegundos { get; set; } = 10;
    public int ExpiracionReservaSegundos { get; set; } = 60;

    /// <summary>Todas las sedes menos la local.</summary>
    public string[] OtrasSedes()
    {
        string[] resultado = Array.Empty<string>();
        foreach (string sede in Sedes)
        {
            if (sede == SedeLocal) continue;
            Array.Resize(ref resultado, resultado.Length + 1);
            resultado[^1] = sede;
        }
        return resultado;
    }
}
