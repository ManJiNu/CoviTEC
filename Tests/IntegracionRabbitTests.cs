using ConviTEC.Messaging;
using ConviTEC.Services.Orders;
using Xunit;

namespace ConviTEC.Tests;

/// <summary>
/// Pruebas con RabbitMQ REAL. Solo se ejecutan si existe la variable de entorno CONVITEC_RABBIT
/// (dirección de RabbitMQ, por ejemplo "localhost"); si no, terminan sin hacer nada.
/// Opcionales: CONVITEC_RABBIT_USER y CONVITEC_RABBIT_PASS (por defecto guest/guest).
/// Cada ejecución usa sedes con nombre único para no mezclarse con mensajes de corridas anteriores.
/// </summary>
public class IntegracionRabbitTests
{
    private static string? Host => Environment.GetEnvironmentVariable("CONVITEC_RABBIT");

    private sealed class Nodo : IAsyncDisposable
    {
        public InventarioFalso Inventario = null!;
        public GlobalFalso Global = new();
        public AlmacenFalso Almacen = new();
        public RabbitMqBus Bus = null!;
        public ManejadorSede Manejador = null!;
        public ServicioPedidos Servicio = null!;

        public ValueTask DisposeAsync() => Bus.DisposeAsync();
    }

    private static async Task<Nodo> CrearAsync(string sede, string[] todas, int existenciasA, bool iniciar = true)
    {
        var opciones = new OpcionesMensajeria
        {
            Host = Host!,
            Usuario = Environment.GetEnvironmentVariable("CONVITEC_RABBIT_USER") ?? "guest",
            Contrasena = Environment.GetEnvironmentVariable("CONVITEC_RABBIT_PASS") ?? "guest",
            SedeLocal = sede,
            Sedes = todas,
            TiempoEsperaReservaSegundos = 5
        };

        var nodo = new Nodo();
        nodo.Bus = new RabbitMqBus(opciones);
        nodo.Inventario = new InventarioFalso(new[] { "A" }, new[] { existenciasA });
        nodo.Manejador = new ManejadorSede(nodo.Bus, nodo.Inventario, nodo.Global, opciones);
        nodo.Servicio = new ServicioPedidos(nodo.Bus, nodo.Manejador, nodo.Inventario, nodo.Global,
            new CatalogoFalso(), new RutasFalsas(), nodo.Almacen, opciones);

        if (iniciar) await nodo.Bus.IniciarAsync(nodo.Manejador.ProcesarAsync);
        return nodo;
    }

    /// <summary>Con RabbitMQ los mensajes llegan de forma asíncrona: se espera hasta que se cumpla la condición.</summary>
    private static async Task EsperarAsync(Func<bool> condicion, int segundos = 10)
    {
        DateTime limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(50);
    }

    private static SolicitudPedido Pedir(string destino, int cantidad) => new()
    {
        Destino = destino,
        Lineas = new[] { new LineaProducto { Codigo = "A", Cantidad = cantidad } }
    };

    [Fact]
    public async Task PedidoEntreDosSedes_ViajaPorRabbitMq()
    {
        if (Host == null) return; // sin RabbitMQ configurado, la prueba no aplica

        string id = Guid.NewGuid().ToString("N")[..6];
        string a = "Alajuela" + id, b = "SanJose" + id;
        string[] todas = { a, b };

        await using Nodo nodoA = await CrearAsync(a, todas, existenciasA: 2);
        await using Nodo nodoB = await CrearAsync(b, todas, existenciasA: 10);

        // Sincronización inicial del inventario global
        await nodoA.Manejador.SincronizarInventarioAsync();
        await nodoB.Manejador.SincronizarInventarioAsync();
        await EsperarAsync(() => nodoA.Global.Existencia(b, "A") == 10 && nodoB.Global.Existencia(a, "A") == 2);
        Assert.Equal(10, nodoA.Global.Existencia(b, "A"));
        Assert.Equal(2, nodoB.Global.Existencia(a, "A"));

        // Pedido que necesita unidades de las dos sedes
        ResultadoPedido r = await nodoA.Servicio.RegistrarAsync(Pedir("Heredia", 5));

        Assert.True(r.Aceptado, r.Motivo);
        await EsperarAsync(() => nodoB.Inventario.Existencia("A") == 7 && nodoA.Global.Existencia(b, "A") == 7);
        Assert.Equal(0, nodoA.Inventario.Existencia("A"));
        Assert.Equal(7, nodoB.Inventario.Existencia("A"));
        Assert.Equal(7, nodoA.Global.Existencia(b, "A"));
        Assert.Equal(0, nodoB.Inventario.Reservado("A"));
    }

    [Fact]
    public async Task SedeProveedoraSinResponder_CancelaElPedidoPorTiempoDeEspera()
    {
        if (Host == null) return;

        string id = Guid.NewGuid().ToString("N")[..6];
        string a = "Alajuela" + id, b = "SanJose" + id;
        string[] todas = { a, b };

        await using Nodo nodoA = await CrearAsync(a, todas, existenciasA: 0);
        // La sede B no se conecta (simula una sede caída): su cola existe pero nadie la consume.
        await using Nodo nodoB = await CrearAsync(b, todas, existenciasA: 10, iniciar: false);
        nodoA.Global.Actualizar(b, "A", 10);

        ResultadoPedido r = await nodoA.Servicio.RegistrarAsync(Pedir("Heredia", 3));

        Assert.False(r.Aceptado);
        Assert.Equal(EstadoPedido.Cancelado, r.Pedido.Estado);
        Assert.Contains(b, r.Motivo);
        Assert.Equal(0, nodoA.Almacen.Pendientes);
    }
}
