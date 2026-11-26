using ConviTEC.Messaging;
using ConviTEC.Services.Orders;
using Xunit;

namespace ConviTEC.Tests;

// ---------- Falsos de las interfaces de las Personas 1 y 2 (sin colecciones nativas) ----------

internal sealed class InventarioFalso : IInventarioLocal
{
    private readonly string[] _codigos;
    public int[] Existencias;
    private string[] _resPedido = Array.Empty<string>();
    private string[] _resCodigo = Array.Empty<string>();
    private int[] _resCantidad = Array.Empty<int>();

    public InventarioFalso(string[] codigos, int[] existencias)
    {
        _codigos = codigos;
        Existencias = existencias;
    }

    private int Indice(string codigo)
    {
        for (int i = 0; i < _codigos.Length; i++) if (_codigos[i] == codigo) return i;
        return -1;
    }

    public int Existencia(string codigo) { int i = Indice(codigo); return i < 0 ? 0 : Existencias[i]; }

    public int Reservado(string codigo)
    {
        int total = 0;
        for (int k = 0; k < _resCodigo.Length; k++) if (_resCodigo[k] == codigo) total += _resCantidad[k];
        return total;
    }

    public int Disponible(string codigo) => Existencia(codigo) - Reservado(codigo);

    public bool Reservar(string idPedido, string codigo, int cantidad)
    {
        if (Disponible(codigo) < cantidad) return false;
        Array.Resize(ref _resPedido, _resPedido.Length + 1); _resPedido[^1] = idPedido;
        Array.Resize(ref _resCodigo, _resCodigo.Length + 1); _resCodigo[^1] = codigo;
        Array.Resize(ref _resCantidad, _resCantidad.Length + 1); _resCantidad[^1] = cantidad;
        return true;
    }

    public void LiberarReservas(string idPedido)
    {
        string[] p = Array.Empty<string>(); string[] c = Array.Empty<string>(); int[] n = Array.Empty<int>();
        for (int k = 0; k < _resPedido.Length; k++)
        {
            if (_resPedido[k] == idPedido) continue;
            Array.Resize(ref p, p.Length + 1); p[^1] = _resPedido[k];
            Array.Resize(ref c, c.Length + 1); c[^1] = _resCodigo[k];
            Array.Resize(ref n, n.Length + 1); n[^1] = _resCantidad[k];
        }
        _resPedido = p; _resCodigo = c; _resCantidad = n;
    }

    public bool ConfirmarReservas(string idPedido)
    {
        for (int k = 0; k < _resPedido.Length; k++)
        {
            if (_resPedido[k] != idPedido) continue;
            int i = Indice(_resCodigo[k]);
            if (i >= 0) Existencias[i] -= _resCantidad[k];
        }
        LiberarReservas(idPedido);
        return true;
    }

    public LineaProducto[] Snapshot()
    {
        var resultado = new LineaProducto[_codigos.Length];
        for (int i = 0; i < _codigos.Length; i++)
            resultado[i] = new LineaProducto { Codigo = _codigos[i], Cantidad = Existencias[i] };
        return resultado;
    }
}

internal sealed class GlobalFalso : IInventarioGlobal
{
    private string[] _sedes = Array.Empty<string>();
    private string[] _codigos = Array.Empty<string>();
    private int[] _cantidades = Array.Empty<int>();

    public int Existencia(string sede, string codigo)
    {
        for (int i = 0; i < _sedes.Length; i++) if (_sedes[i] == sede && _codigos[i] == codigo) return _cantidades[i];
        return 0;
    }

    public void Actualizar(string sede, string codigo, int cantidad)
    {
        for (int i = 0; i < _sedes.Length; i++)
        {
            if (_sedes[i] != sede || _codigos[i] != codigo) continue;
            _cantidades[i] = cantidad;
            return;
        }
        Array.Resize(ref _sedes, _sedes.Length + 1); _sedes[^1] = sede;
        Array.Resize(ref _codigos, _codigos.Length + 1); _codigos[^1] = codigo;
        Array.Resize(ref _cantidades, _cantidades.Length + 1); _cantidades[^1] = cantidad;
    }
}

internal sealed class CatalogoFalso : ICatalogo
{
    public bool TryObtenerPrecio(string codigo, out decimal precio)
    {
        precio = 1000m;
        return codigo != "NOEXISTE";
    }
}

/// <summary>Liberia solo alcanza Cañas (componente aislado); el resto de sedes alcanza todo.</summary>
internal sealed class RutasFalsas : IRutas
{
    public ResultadoRuta Verificar(string sedeOrigen, string destino)
    {
        if (sedeOrigen == "Liberia" && destino != "Cañas")
            return new ResultadoRuta(false, 0, $"{destino} no es alcanzable desde Liberia (componente aislado).");
        return new ResultadoRuta(true, 60, "");
    }
}

internal sealed class AlmacenFalso : IAlmacenPedidos
{
    public int Pendientes;
    public int Historial;
    public void EncolarPendiente(Pedido pedido) => Pendientes++;
    public void RegistrarHistorial(Pedido pedido) => Historial++;
}

internal sealed class SedeDePrueba
{
    public InventarioFalso Inventario;
    public GlobalFalso Global = new();
    public AlmacenFalso Almacen = new();
    public ManejadorSede Manejador;
    public ServicioPedidos Servicio;
    public BusEnMemoria Bus;

    public SedeDePrueba(RedEnMemoria red, string sede, string[] todas, string[] codigos, int[] existencias)
    {
        var opciones = new OpcionesMensajeria { SedeLocal = sede, Sedes = todas, TiempoEsperaReservaSegundos = 2 };
        Bus = red.CrearBus(sede, todas);
        Inventario = new InventarioFalso(codigos, existencias);
        Manejador = new ManejadorSede(Bus, Inventario, Global, opciones);
        Servicio = new ServicioPedidos(Bus, Manejador, Inventario, Global, new CatalogoFalso(), new RutasFalsas(), Almacen, opciones);
    }

    public Task IniciarAsync() => Bus.IniciarAsync(Manejador.ProcesarAsync);
}

public class PedidosTests
{
    private static readonly string[] Sedes = { "Liberia", "Alajuela", "SanJose" };

    private static async Task<(SedeDePrueba liberia, SedeDePrueba alajuela, SedeDePrueba sanJose)> CrearRedAsync(
        int alajuelaA, int sanJoseA)
    {
        var red = new RedEnMemoria();
        var liberia = new SedeDePrueba(red, "Liberia", Sedes, new[] { "A" }, new[] { 0 });
        var alajuela = new SedeDePrueba(red, "Alajuela", Sedes, new[] { "A" }, new[] { alajuelaA });
        var sanJose = new SedeDePrueba(red, "SanJose", Sedes, new[] { "A" }, new[] { sanJoseA });

        await liberia.IniciarAsync();
        await alajuela.IniciarAsync();
        await sanJose.IniciarAsync();

        // Sincronización del inventario global por mensajes
        await liberia.Manejador.SincronizarInventarioAsync();
        await alajuela.Manejador.SincronizarInventarioAsync();
        await sanJose.Manejador.SincronizarInventarioAsync();
        return (liberia, alajuela, sanJose);
    }

    private static SolicitudPedido Pedir(string destino, string codigo, int cantidad) => new()
    {
        Destino = destino,
        Lineas = new[] { new LineaProducto { Codigo = codigo, Cantidad = cantidad } }
    };

    [Fact]
    public async Task SincronizacionInicial_LlenaElInventarioGlobal()
    {
        var (liberia, alajuela, _) = await CrearRedAsync(alajuelaA: 2, sanJoseA: 10);

        Assert.Equal(2, liberia.Global.Existencia("Alajuela", "A"));
        Assert.Equal(10, alajuela.Global.Existencia("SanJose", "A"));
    }

    [Fact]
    public async Task PedidoConProductosDeVariasSedes_SeAceptaYRebajaLosInventarios()
    {
        var (_, alajuela, sanJose) = await CrearRedAsync(alajuelaA: 2, sanJoseA: 10);

        ResultadoPedido r = await alajuela.Servicio.RegistrarAsync(Pedir("Heredia", "A", 5));

        Assert.True(r.Aceptado, r.Motivo);
        Assert.Equal(EstadoPedido.PendienteEntrega, r.Pedido.Estado);
        Assert.Equal(2, r.Pedido.SedesParticipantes.Length);
        Assert.Equal(0, alajuela.Inventario.Existencia("A"));   // 2 locales
        Assert.Equal(7, sanJose.Inventario.Existencia("A"));    // 3 de San José
        Assert.Equal(7, alajuela.Global.Existencia("SanJose", "A")); // copia global actualizada por mensaje
        Assert.Equal(1, alajuela.Almacen.Pendientes);
    }

    [Fact]
    public async Task DestinoInalcanzable_SeRechazaConExplicacionYNoReservaNada()
    {
        var (liberia, _, sanJose) = await CrearRedAsync(alajuelaA: 2, sanJoseA: 10);

        ResultadoPedido r = await liberia.Servicio.RegistrarAsync(Pedir("Heredia", "A", 3));

        Assert.False(r.Aceptado);
        Assert.Equal(EstadoPedido.Rechazado, r.Pedido.Estado);
        Assert.Contains("Heredia", r.Motivo);
        Assert.Equal(0, sanJose.Inventario.Reservado("A"));
        Assert.Equal(1, liberia.Almacen.Historial); // los rechazados también quedan en el historial
    }

    [Fact]
    public async Task ProveedorSinStockReal_CancelaElPedidoCompletoYLiberaReservas()
    {
        var (_, alajuela, sanJose) = await CrearRedAsync(alajuelaA: 2, sanJoseA: 0);
        alajuela.Global.Actualizar("SanJose", "A", 5); // copia global desactualizada

        ResultadoPedido r = await alajuela.Servicio.RegistrarAsync(Pedir("Heredia", "A", 3));

        Assert.False(r.Aceptado);
        Assert.Equal(EstadoPedido.Cancelado, r.Pedido.Estado);
        Assert.Equal(0, alajuela.Inventario.Reservado("A")); // la reserva local se liberó
        Assert.Equal(2, alajuela.Inventario.Existencia("A")); // nada se descontó
        Assert.Equal(0, alajuela.Almacen.Pendientes);
    }

    [Fact]
    public async Task SedeProveedoraCaida_CancelaPorTiempoDeEspera()
    {
        var (_, alajuela, sanJose) = await CrearRedAsync(alajuelaA: 0, sanJoseA: 10);
        sanJose.Bus.Caida = true;

        ResultadoPedido r = await alajuela.Servicio.RegistrarAsync(Pedir("Heredia", "A", 3));

        Assert.False(r.Aceptado);
        Assert.Equal(EstadoPedido.Cancelado, r.Pedido.Estado);
        Assert.Contains("SanJose", r.Motivo);
    }

    [Fact]
    public async Task ReservaSinConfirmar_ExpiraYLiberaElStock()
    {
        var (_, _, sanJose) = await CrearRedAsync(alajuelaA: 0, sanJoseA: 10);
        var opciones = new OpcionesMensajeria { SedeLocal = "SanJose", Sedes = Sedes, ExpiracionReservaSegundos = -1 };
        var manejador = new ManejadorSede(sanJose.Bus, sanJose.Inventario, sanJose.Global, opciones);

        Assert.True(manejador.ReservarLocal("P-1", new[] { new LineaProducto { Codigo = "A", Cantidad = 4 } }, out _));
        Assert.Equal(4, sanJose.Inventario.Reservado("A"));

        Assert.Equal(1, manejador.LiberarExpiradas());
        Assert.Equal(0, sanJose.Inventario.Reservado("A"));
    }

    [Fact]
    public async Task ReservaRepetida_EsIdempotente()
    {
        var (_, _, sanJose) = await CrearRedAsync(alajuelaA: 0, sanJoseA: 10);
        var lineas = new[] { new LineaProducto { Codigo = "A", Cantidad = 4 } };

        Assert.True(sanJose.Manejador.ReservarLocal("P-1", lineas, out _));
        Assert.True(sanJose.Manejador.ReservarLocal("P-1", lineas, out _)); // mensaje duplicado
        Assert.Equal(4, sanJose.Inventario.Reservado("A"));
    }

    [Fact]
    public async Task PedidoInvalido_SeRechaza()
    {
        var (_, alajuela, _) = await CrearRedAsync(alajuelaA: 5, sanJoseA: 5);

        Assert.False((await alajuela.Servicio.RegistrarAsync(null)).Aceptado);
        Assert.False((await alajuela.Servicio.RegistrarAsync(Pedir("Heredia", "A", 0))).Aceptado);
        Assert.False((await alajuela.Servicio.RegistrarAsync(Pedir("Heredia", "NOEXISTE", 1))).Aceptado);
        Assert.False((await alajuela.Servicio.RegistrarAsync(Pedir("", "A", 1))).Aceptado);
        Assert.False((await alajuela.Servicio.RegistrarAsync(Pedir("Heredia", "A", 99))).Aceptado);
    }
}
