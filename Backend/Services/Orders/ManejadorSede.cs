using ConviTEC.Messaging;

namespace ConviTEC.Services.Orders;

/// <summary>
/// Lado "proveedor" de una sede: es la ÚNICA autoridad sobre su inventario local.
/// Atiende los mensajes entrantes (reservar, confirmar, cancelar, sincronizar inventario).
/// </summary>
public sealed class ManejadorSede
{
    private sealed class ReservaActiva
    {
        public string IdPedido = "";
        public DateTime FechaUtc;
    }

    private readonly IBusMensajes _bus;
    private readonly IInventarioLocal _inventario;
    private readonly IInventarioGlobal _global;
    private readonly OpcionesMensajeria _opciones;
    private readonly object _candado = new();
    private ReservaActiva[] _reservas = Array.Empty<ReservaActiva>();

    public ManejadorSede(IBusMensajes bus, IInventarioLocal inventario, IInventarioGlobal global, OpcionesMensajeria opciones)
    {
        _bus = bus;
        _inventario = inventario;
        _global = global;
        _opciones = opciones;
    }

    /// <summary>ServicioPedidos se suscribe aquí para recibir las respuestas a sus solicitudes de reserva.</summary>
    public Action<Mensaje>? RespuestaReservaRecibida { get; set; }

    /// <summary>Se pasa al bus: bus.IniciarAsync(manejador.ProcesarAsync).</summary>
    public async Task ProcesarAsync(Mensaje mensaje)
    {
        try
        {
            switch (mensaje.Tipo)
            {
                case TipoMensaje.SolicitudReserva:
                    await AtenderSolicitudAsync(mensaje);
                    break;
                case TipoMensaje.ConfirmarReserva:
                    await ConfirmarLocalAsync(mensaje.IdPedido);
                    break;
                case TipoMensaje.CancelarReserva:
                    CancelarLocal(mensaje.IdPedido);
                    break;
                case TipoMensaje.ActualizarInventario:
                case TipoMensaje.RespuestaInventario:
                    AplicarActualizacion(mensaje);
                    break;
                case TipoMensaje.ConsultarInventario:
                    await ResponderConsultaAsync(mensaje);
                    break;
                case TipoMensaje.RespuestaReserva:
                    RespuestaReservaRecibida?.Invoke(mensaje);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{_opciones.SedeLocal}] Error procesando {mensaje.Tipo}: {ex.Message}");
        }
    }

    // ---------- Operaciones locales (las usan tanto los mensajes como ServicioPedidos) ----------

    /// <summary>Reserva todas las líneas o ninguna. Es idempotente: repetir el mismo pedido responde éxito.</summary>
    public bool ReservarLocal(string idPedido, LineaProducto[] lineas, out string motivo)
    {
        lock (_candado)
        {
            motivo = "";
            if (Buscar(idPedido) >= 0) return true;

            foreach (LineaProducto linea in lineas)
            {
                if (linea.Cantidad <= 0 || !_inventario.Reservar(idPedido, linea.Codigo, linea.Cantidad))
                {
                    _inventario.LiberarReservas(idPedido);
                    motivo = $"{_opciones.SedeLocal}: inventario insuficiente de {linea.Codigo}.";
                    return false;
                }
            }

            Arreglos.Anexar(ref _reservas, new ReservaActiva { IdPedido = idPedido, FechaUtc = DateTime.UtcNow });
            return true;
        }
    }

    /// <summary>Descuenta el inventario de la reserva y avisa a las demás sedes.</summary>
    public async Task ConfirmarLocalAsync(string idPedido)
    {
        lock (_candado)
        {
            int i = Buscar(idPedido);
            if (i < 0) return; // ya confirmada, cancelada o expirada
            Arreglos.QuitarEn(ref _reservas, i);
            if (!_inventario.ConfirmarReservas(idPedido)) return;
        }
        await PublicarInventarioPropioAsync(idPedido);
    }

    public void CancelarLocal(string idPedido)
    {
        lock (_candado)
        {
            _inventario.LiberarReservas(idPedido);
            int i = Buscar(idPedido);
            if (i >= 0) Arreglos.QuitarEn(ref _reservas, i);
        }
    }

    /// <summary>Libera las reservas que llevan demasiado tiempo sin confirmarse (p. ej. la sede solicitante se cayó).</summary>
    public int LiberarExpiradas()
    {
        int liberadas = 0;
        lock (_candado)
        {
            DateTime limite = DateTime.UtcNow.AddSeconds(-_opciones.ExpiracionReservaSegundos);
            for (int i = _reservas.Length - 1; i >= 0; i--)
            {
                if (_reservas[i].FechaUtc >= limite) continue;
                _inventario.LiberarReservas(_reservas[i].IdPedido);
                Arreglos.QuitarEn(ref _reservas, i);
                liberadas++;
            }
        }
        return liberadas;
    }

    /// <summary>Tarea de fondo: revisa las reservas expiradas cada 5 segundos.</summary>
    public async Task EjecutarExpiracionAsync(CancellationToken ct)
    {
        using var temporizador = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await temporizador.WaitForNextTickAsync(ct)) LiberarExpiradas();
        }
        catch (OperationCanceledException)
        {
            // cierre normal
        }
    }

    /// <summary>Al arrancar: publica el inventario propio y pide el de las demás sedes.</summary>
    public async Task SincronizarInventarioAsync()
    {
        await PublicarInventarioPropioAsync("");
        foreach (string sede in _bus.OtrasSedes)
        {
            await EnviarSeguroAsync(Mensaje.Crear(TipoMensaje.ConsultarInventario, _opciones.SedeLocal, sede, ""));
        }
    }

    // ---------- Mensajes entrantes ----------

    private async Task AtenderSolicitudAsync(Mensaje mensaje)
    {
        SolicitudReserva? solicitud = mensaje.LeerContenido<SolicitudReserva>();
        RespuestaReserva respuesta;

        if (solicitud?.Lineas == null || solicitud.Lineas.Length == 0)
        {
            respuesta = new RespuestaReserva { Exito = false, Motivo = "Solicitud de reserva inválida." };
        }
        else
        {
            bool ok = ReservarLocal(mensaje.IdPedido, solicitud.Lineas, out string motivo);
            respuesta = new RespuestaReserva { Exito = ok, Motivo = motivo };
        }

        await EnviarSeguroAsync(Mensaje.Crear(
            TipoMensaje.RespuestaReserva, _opciones.SedeLocal, mensaje.Origen, mensaje.IdPedido, respuesta));
    }

    private void AplicarActualizacion(Mensaje mensaje)
    {
        ActualizacionInventario? actualizacion = mensaje.LeerContenido<ActualizacionInventario>();
        if (actualizacion == null || actualizacion.Sede == _opciones.SedeLocal) return;
        foreach (LineaProducto linea in actualizacion.Lineas)
        {
            _global.Actualizar(actualizacion.Sede, linea.Codigo, linea.Cantidad);
        }
    }

    private async Task ResponderConsultaAsync(Mensaje mensaje)
    {
        var contenido = new ActualizacionInventario { Sede = _opciones.SedeLocal, Lineas = _inventario.Snapshot() };
        await EnviarSeguroAsync(Mensaje.Crear(
            TipoMensaje.RespuestaInventario, _opciones.SedeLocal, mensaje.Origen, "", contenido));
    }

    // ---------- Auxiliares ----------

    private async Task PublicarInventarioPropioAsync(string idPedido)
    {
        LineaProducto[] existencias = _inventario.Snapshot();
        foreach (LineaProducto linea in existencias)
        {
            _global.Actualizar(_opciones.SedeLocal, linea.Codigo, linea.Cantidad);
        }

        var contenido = new ActualizacionInventario { Sede = _opciones.SedeLocal, Lineas = existencias };
        foreach (string sede in _bus.OtrasSedes)
        {
            await EnviarSeguroAsync(Mensaje.Crear(
                TipoMensaje.ActualizarInventario, _opciones.SedeLocal, sede, idPedido, contenido));
        }
    }

    /// <summary>Una sede caída no debe romper el resto: el error se registra y se sigue.</summary>
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

    private int Buscar(string idPedido)
    {
        for (int i = 0; i < _reservas.Length; i++)
        {
            if (_reservas[i].IdPedido == idPedido) return i;
        }
        return -1;
    }
}
