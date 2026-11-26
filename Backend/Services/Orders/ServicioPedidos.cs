using ConviTEC.Messaging;

namespace ConviTEC.Services.Orders;

/// <summary>
/// Lado "solicitante": registra un pedido, decide de qué sedes sale cada producto,
/// reserva (local y remoto), y confirma o cancela el pedido COMPLETO.
///
/// Orden de validación: productos -> ruta -> disponibilidad -> reservas.
/// La ruta se verifica antes de reservar para no enviar mensajes inútiles; el camino de
/// cancelación (reserva fallida o expirada) libera todas las reservas de todas las sedes.
/// </summary>
public sealed class ServicioPedidos
{
    private sealed class PlanSede
    {
        public string Sede = "";
        public LineaProducto[] Lineas = Array.Empty<LineaProducto>();
    }

    private sealed class Espera
    {
        public string IdPedido = "";
        public string Sede = "";
        public TaskCompletionSource<RespuestaReserva> Fuente = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IBusMensajes _bus;
    private readonly ManejadorSede _manejador;
    private readonly IInventarioLocal _inventario;
    private readonly IInventarioGlobal _global;
    private readonly ICatalogo _catalogo;
    private readonly IRutas _rutas;
    private readonly IAlmacenPedidos _almacen;
    private readonly OpcionesMensajeria _opciones;

    private readonly object _candadoEsperas = new();
    private Espera[] _esperas = Array.Empty<Espera>();

    public ServicioPedidos(
        IBusMensajes bus, ManejadorSede manejador, IInventarioLocal inventario, IInventarioGlobal global,
        ICatalogo catalogo, IRutas rutas, IAlmacenPedidos almacen, OpcionesMensajeria opciones)
    {
        _bus = bus;
        _manejador = manejador;
        _inventario = inventario;
        _global = global;
        _catalogo = catalogo;
        _rutas = rutas;
        _almacen = almacen;
        _opciones = opciones;
        _manejador.RespuestaReservaRecibida = AlRecibirRespuesta;
    }

    public async Task<ResultadoPedido> RegistrarAsync(SolicitudPedido? solicitud, CancellationToken ct = default)
    {
        string local = _opciones.SedeLocal;
        var pedido = new Pedido
        {
            Id = $"P-{local}-{Guid.NewGuid().ToString("N")[..8]}",
            Fecha = DateTime.Now,
            SedeRegistro = local,
            Destino = solicitud?.Destino?.Trim() ?? "",
            Estado = EstadoPedido.PendienteReserva
        };

        // 1. Validar la entrada
        if (solicitud?.Lineas == null || solicitud.Lineas.Length == 0)
            return Cerrar(pedido, EstadoPedido.Rechazado, "El pedido no contiene productos.");
        if (pedido.Destino.Length == 0)
            return Cerrar(pedido, EstadoPedido.Rechazado, "Debe indicar el punto de entrega.");

        LineaProducto[]? lineas = Consolidar(solicitud.Lineas, out string errorLineas);
        if (lineas == null) return Cerrar(pedido, EstadoPedido.Rechazado, errorLineas);

        decimal[] precios = new decimal[lineas.Length];
        for (int i = 0; i < lineas.Length; i++)
        {
            if (!_catalogo.TryObtenerPrecio(lineas[i].Codigo, out precios[i]))
                return Cerrar(pedido, EstadoPedido.Rechazado, $"El producto {lineas[i].Codigo} no existe en el catálogo.");
        }

        // 2. Verificar la ruta
        ResultadoRuta ruta = _rutas.Verificar(local, pedido.Destino);
        if (!ruta.Alcanzable)
        {
            string motivo = string.IsNullOrWhiteSpace(ruta.Motivo)
                ? $"No existe una ruta desde {local} hasta {pedido.Destino}."
                : ruta.Motivo;
            return Cerrar(pedido, EstadoPedido.Rechazado, motivo);
        }

        // 3. Decidir de qué sede sale cada producto (primero la local, luego las demás)
        string[] orden = new[] { local };
        foreach (string sede in _bus.OtrasSedes) Arreglos.Anexar(ref orden, sede);

        PlanSede[] plan = Array.Empty<PlanSede>();
        ItemPedido[] items = Array.Empty<ItemPedido>();

        for (int i = 0; i < lineas.Length; i++)
        {
            string codigo = lineas[i].Codigo;
            int faltan = lineas[i].Cantidad;

            foreach (string sede in orden)
            {
                if (faltan == 0) break;
                int disponible = sede == local ? _inventario.Disponible(codigo) : _global.Existencia(sede, codigo);
                int tomar = Math.Min(faltan, disponible);
                if (tomar <= 0) continue;

                AgregarAlPlan(ref plan, sede, codigo, tomar);
                Arreglos.Anexar(ref items, new ItemPedido
                {
                    Codigo = codigo, Cantidad = tomar, PrecioUnitario = precios[i], SedeOrigen = sede
                });
                faltan -= tomar;
            }

            if (faltan > 0)
                return Cerrar(pedido, EstadoPedido.Rechazado,
                    $"Inventario insuficiente de {codigo}: faltan {faltan} unidades entre todas las sedes.");
        }

        pedido.Items = items;
        string[] participantes = Array.Empty<string>();
        foreach (PlanSede p in plan) Arreglos.Anexar(ref participantes, p.Sede);
        pedido.SedesParticipantes = participantes;

        // 4. Reservar: primero la sede local, luego las remotas
        foreach (PlanSede p in plan)
        {
            if (p.Sede != local) continue;
            if (!_manejador.ReservarLocal(pedido.Id, p.Lineas, out string motivoLocal))
                return Cerrar(pedido, EstadoPedido.Cancelado, motivoLocal);
        }

        Espera[] esperas = Array.Empty<Espera>();
        string[] remotas = Array.Empty<string>();
        foreach (PlanSede p in plan)
        {
            if (p.Sede == local) continue;
            Arreglos.Anexar(ref remotas, p.Sede);

            Espera espera = RegistrarEspera(pedido.Id, p.Sede);
            Arreglos.Anexar(ref esperas, espera);
            try
            {
                await _bus.EnviarAsync(Mensaje.Crear(TipoMensaje.SolicitudReserva, local, p.Sede, pedido.Id,
                    new SolicitudReserva { Lineas = p.Lineas }), ct);
            }
            catch (Exception)
            {
                espera.Fuente.TrySetResult(new RespuestaReserva
                {
                    Exito = false, Motivo = $"No se pudo contactar a la sede {p.Sede}."
                });
            }
        }

        string? fallo = null;
        if (esperas.Length > 0)
        {
            Task[] tareas = new Task[esperas.Length];
            for (int i = 0; i < esperas.Length; i++) tareas[i] = esperas[i].Fuente.Task;

            try
            {
                await Task.WhenAll(tareas).WaitAsync(TimeSpan.FromSeconds(_opciones.TiempoEsperaReservaSegundos), ct);
            }
            catch (TimeoutException)
            {
                // se evalúa abajo qué sedes no respondieron
            }

            foreach (Espera espera in esperas)
            {
                if (!espera.Fuente.Task.IsCompletedSuccessfully)
                {
                    fallo ??= $"La sede {espera.Sede} no respondió a tiempo.";
                }
                else if (!espera.Fuente.Task.Result.Exito)
                {
                    fallo ??= espera.Fuente.Task.Result.Motivo;
                }
            }
            QuitarEsperas(pedido.Id);
        }

        // 5a. Algo falló: se cancela el pedido completo y se liberan todas las reservas
        if (fallo != null)
        {
            foreach (string sede in remotas)
            {
                await EnviarSeguroAsync(Mensaje.Crear(TipoMensaje.CancelarReserva, local, sede, pedido.Id,
                    new CancelacionReserva { Motivo = fallo }));
            }
            _manejador.CancelarLocal(pedido.Id);
            return Cerrar(pedido, EstadoPedido.Cancelado, fallo);
        }

        // 5b. Todo reservado: se confirma, se rebajan los inventarios y se encola para entrega
        pedido.Estado = EstadoPedido.Reservado;
        foreach (string sede in remotas)
        {
            await EnviarSeguroAsync(Mensaje.Crear(TipoMensaje.ConfirmarReserva, local, sede, pedido.Id));
        }
        await _manejador.ConfirmarLocalAsync(pedido.Id);

        pedido.Estado = EstadoPedido.PendienteEntrega;
        pedido.TiempoTrasladoMin = ruta.Minutos;
        _almacen.EncolarPendiente(pedido);
        _almacen.RegistrarHistorial(pedido);
        return new ResultadoPedido(true, "Pedido aceptado.", pedido);
    }

    // ---------- Auxiliares ----------

    private ResultadoPedido Cerrar(Pedido pedido, EstadoPedido estado, string motivo)
    {
        pedido.Estado = estado;
        pedido.Motivo = motivo;
        _almacen.RegistrarHistorial(pedido);
        return new ResultadoPedido(false, motivo, pedido);
    }

    /// <summary>Suma las líneas con el mismo producto y rechaza cantidades inválidas.</summary>
    private static LineaProducto[]? Consolidar(LineaProducto[] entrada, out string error)
    {
        error = "";
        LineaProducto[] resultado = Array.Empty<LineaProducto>();

        foreach (LineaProducto linea in entrada)
        {
            if (linea == null || string.IsNullOrWhiteSpace(linea.Codigo))
            {
                error = "Hay una línea sin código de producto.";
                return null;
            }
            if (linea.Cantidad <= 0)
            {
                error = $"La cantidad de {linea.Codigo} debe ser mayor que cero.";
                return null;
            }

            bool encontrada = false;
            foreach (LineaProducto existente in resultado)
            {
                if (existente.Codigo != linea.Codigo) continue;
                existente.Cantidad += linea.Cantidad;
                encontrada = true;
                break;
            }
            if (!encontrada)
                Arreglos.Anexar(ref resultado, new LineaProducto { Codigo = linea.Codigo.Trim(), Cantidad = linea.Cantidad });
        }
        return resultado;
    }

    private static void AgregarAlPlan(ref PlanSede[] plan, string sede, string codigo, int cantidad)
    {
        foreach (PlanSede p in plan)
        {
            if (p.Sede != sede) continue;
            Arreglos.Anexar(ref p.Lineas, new LineaProducto { Codigo = codigo, Cantidad = cantidad });
            return;
        }
        var nuevo = new PlanSede { Sede = sede };
        Arreglos.Anexar(ref nuevo.Lineas, new LineaProducto { Codigo = codigo, Cantidad = cantidad });
        Arreglos.Anexar(ref plan, nuevo);
    }

    private async Task EnviarSeguroAsync(Mensaje mensaje)
    {
        try
        {
            await _bus.EnviarAsync(mensaje);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{_opciones.SedeLocal}] No se pudo enviar {mensaje.Tipo} a {mensaje.Destino}: {ex.Message}");
        }
    }

    private Espera RegistrarEspera(string idPedido, string sede)
    {
        var espera = new Espera { IdPedido = idPedido, Sede = sede };
        lock (_candadoEsperas) Arreglos.Anexar(ref _esperas, espera);
        return espera;
    }

    private void QuitarEsperas(string idPedido)
    {
        lock (_candadoEsperas)
        {
            for (int i = _esperas.Length - 1; i >= 0; i--)
            {
                if (_esperas[i].IdPedido == idPedido) Arreglos.QuitarEn(ref _esperas, i);
            }
        }
    }

    private void AlRecibirRespuesta(Mensaje mensaje)
    {
        RespuestaReserva respuesta = mensaje.LeerContenido<RespuestaReserva>()
            ?? new RespuestaReserva { Exito = false, Motivo = "Respuesta vacía." };

        Espera? destino = null;
        lock (_candadoEsperas)
        {
            foreach (Espera e in _esperas)
            {
                if (e.IdPedido == mensaje.IdPedido && e.Sede == mensaje.Origen) { destino = e; break; }
            }
        }
        destino?.Fuente.TrySetResult(respuesta);
    }
}
