using System.Text.Json;

namespace ConviTEC.Messaging;

/// <summary>Protocolo de mensajes entre sedes.</summary>
public enum TipoMensaje
{
    SolicitudReserva,     // sede del pedido -> proveedora        (contenido: SolicitudReserva)
    RespuestaReserva,     // proveedora -> sede del pedido        (contenido: RespuestaReserva)
    ConfirmarReserva,     // sede del pedido -> proveedoras       (sin contenido)
    CancelarReserva,      // sede del pedido -> proveedoras       (contenido: CancelacionReserva)
    ActualizarInventario, // sede -> todas                        (contenido: ActualizacionInventario)
    ConsultarInventario,  // sede -> todas (al arrancar)          (sin contenido)
    RespuestaInventario   // sede -> solicitante                  (contenido: ActualizacionInventario)
}

public sealed class LineaProducto
{
    public string Codigo { get; set; } = "";
    public int Cantidad { get; set; }
}

public sealed class SolicitudReserva
{
    public LineaProducto[] Lineas { get; set; } = Array.Empty<LineaProducto>();
}

public sealed class RespuestaReserva
{
    public bool Exito { get; set; }
    public string Motivo { get; set; } = "";
}

public sealed class CancelacionReserva
{
    public string Motivo { get; set; } = "";
}

/// <summary>Existencias reales de una sede (cantidades absolutas, por eso es idempotente).</summary>
public sealed class ActualizacionInventario
{
    public string Sede { get; set; } = "";
    public LineaProducto[] Lineas { get; set; } = Array.Empty<LineaProducto>();
}

/// <summary>Sobre que viaja por RabbitMQ (se serializa como JSON).</summary>
public sealed class Mensaje
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TipoMensaje Tipo { get; set; }
    public string Origen { get; set; } = "";
    public string Destino { get; set; } = "";
    public string IdPedido { get; set; } = "";
    public string Contenido { get; set; } = "";
    public DateTime FechaUtc { get; set; } = DateTime.UtcNow;

    public static Mensaje Crear<T>(TipoMensaje tipo, string origen, string destino, string idPedido, T contenido)
    {
        return new Mensaje
        {
            Tipo = tipo,
            Origen = origen,
            Destino = destino,
            IdPedido = idPedido,
            Contenido = JsonSerializer.Serialize(contenido)
        };
    }

    public static Mensaje Crear(TipoMensaje tipo, string origen, string destino, string idPedido)
    {
        return new Mensaje { Tipo = tipo, Origen = origen, Destino = destino, IdPedido = idPedido };
    }

    public T? LeerContenido<T>()
    {
        if (string.IsNullOrWhiteSpace(Contenido)) return default;
        return JsonSerializer.Deserialize<T>(Contenido);
    }
}
