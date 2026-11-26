using ConviTEC.Messaging;

namespace ConviTEC.Services.Orders;

// NOTA: estas clases son provisionales. Si la Persona 1 ya tiene Pedido/ItemPedido en Backend/Models,
// se unifican y se borra este archivo.

public enum EstadoPedido
{
    PendienteReserva,
    Reservado,
    PendienteEntrega,
    Entregado,
    Cancelado,
    Rechazado
}

public sealed class ItemPedido
{
    public string Codigo { get; set; } = "";
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }

    /// <summary>Sede que suministra estas unidades.</summary>
    public string SedeOrigen { get; set; } = "";
}

public sealed class Pedido
{
    public string Id { get; set; } = "";
    public DateTime Fecha { get; set; } = DateTime.Now;
    public EstadoPedido Estado { get; set; } = EstadoPedido.PendienteReserva;
    public string SedeRegistro { get; set; } = "";
    public string Destino { get; set; } = "";
    public ItemPedido[] Items { get; set; } = Array.Empty<ItemPedido>();
    public string[] SedesParticipantes { get; set; } = Array.Empty<string>();
    public int TiempoTrasladoMin { get; set; }

    /// <summary>Razón del rechazo o cancelación (vacío si fue aceptado).</summary>
    public string Motivo { get; set; } = "";

    public decimal MontoTotal
    {
        get
        {
            decimal total = 0;
            foreach (ItemPedido item in Items) total += item.Cantidad * item.PrecioUnitario;
            return total;
        }
    }
}

/// <summary>Lo que envía el frontend al registrar un pedido.</summary>
public sealed class SolicitudPedido
{
    public string Destino { get; set; } = "";
    public LineaProducto[] Lineas { get; set; } = Array.Empty<LineaProducto>();
}

public sealed record ResultadoPedido(bool Aceptado, string Motivo, Pedido Pedido);

/// <summary>Utilidades sobre arreglos nativos (no se usan colecciones del lenguaje).</summary>
internal static class Arreglos
{
    public static void Anexar<T>(ref T[] arreglo, T elemento)
    {
        Array.Resize(ref arreglo, arreglo.Length + 1);
        arreglo[^1] = elemento;
    }

    public static void QuitarEn<T>(ref T[] arreglo, int indice)
    {
        var nuevo = new T[arreglo.Length - 1];
        for (int k = 0, j = 0; k < arreglo.Length; k++)
        {
            if (k != indice) nuevo[j++] = arreglo[k];
        }
        arreglo = nuevo;
    }
}
