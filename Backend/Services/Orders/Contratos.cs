using ConviTEC.Messaging;

namespace ConviTEC.Services.Orders;

// CONTRATOS que la Persona 3 necesita de las demás personas.
// Cada persona implementa su interfaz; mientras tanto las pruebas usan versiones falsas.

/// <summary>Persona 1: inventario de ESTA sede, con reservas por pedido.</summary>
public interface IInventarioLocal
{
    /// <summary>Existencias menos las unidades reservadas por pedidos en curso.</summary>
    int Disponible(string codigoProducto);

    /// <summary>Reserva unidades para un pedido. false si no alcanzan (no reserva nada).</summary>
    bool Reservar(string idPedido, string codigoProducto, int cantidad);

    /// <summary>Libera todas las reservas del pedido. No falla si no existen.</summary>
    void LiberarReservas(string idPedido);

    /// <summary>Descuenta de las existencias las reservas del pedido y persiste el inventario.</summary>
    bool ConfirmarReservas(string idPedido);

    /// <summary>Existencias reales (sin descontar reservas) de todos los productos de la sede.</summary>
    LineaProducto[] Snapshot();
}

/// <summary>Persona 1: copia local del inventario de todas las sedes (se alimenta por mensajes).</summary>
public interface IInventarioGlobal
{
    int Existencia(string sede, string codigoProducto);
    void Actualizar(string sede, string codigoProducto, int cantidad);
}

/// <summary>Persona 1: catálogo de productos.</summary>
public interface ICatalogo
{
    bool TryObtenerPrecio(string codigoProducto, out decimal precio);
}

/// <summary>Persona 2: verificación de rutas sobre el grafo (Dijkstra).</summary>
public interface IRutas
{
    ResultadoRuta Verificar(string sedeOrigen, string destino);
}

/// <summary>Resultado de verificar una ruta. Motivo explica el rechazo cuando no es alcanzable.</summary>
public sealed record ResultadoRuta(bool Alcanzable, int Minutos, string Motivo);

/// <summary>Persona 1: estructuras de pedidos pendientes (cola) e historial.</summary>
public interface IAlmacenPedidos
{
    void EncolarPendiente(Pedido pedido);

    /// <summary>Guarda el pedido en el historial sea cual sea su estado (aceptado, cancelado o rechazado).</summary>
    void RegistrarHistorial(Pedido pedido);
}
