// Punto de entrada compartido (todos los integrantes).
// TODO (integración): registrar aquí las implementaciones reales de IInventarioLocal,
// IInventarioGlobal, ICatalogo, IRutas e IAlmacenPedidos (Personas 1 y 2), crear
// RabbitMqBus + ManejadorSede + ServicioPedidos y agregar los controladores (Persona 4).

var builder = WebApplication.CreateBuilder(args);

// Credenciales propias de cada computadora: no se suben a Git (ver .gitignore).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

var app = builder.Build();

app.MapGet("/health", () => "ok");

app.Run();
