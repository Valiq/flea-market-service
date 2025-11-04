using flea_market_service.Core.Bots;
using flea_market_service.Core.Services;
using System.Text;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.

        builder.Services.AddControllers();
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod() // Разрешаем все методы (GET, POST, OPTIONS)
                      .AllowAnyHeader();
            });
        });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseCors("AllowAll");
        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();

        // Запускаем scheduled tasks и ботов
        var scheduledTasksService = new ScheduledTasksService(builder.Configuration);
        scheduledTasksService.TaskStart();

        var botManager = new OffersManagerBot(builder.Configuration);
        _ = botManager.StartBotAsync();

        var botDelivery = new OffersDeliveryBot(builder.Configuration);
        _ = botDelivery.StartBotAsync();

        // Добавляем остановку таймера при завершении приложения
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            scheduledTasksService.Stop();
            _ = botManager.StopBotAsync();
            _ = botDelivery.StopBotAsync();
        });

        app.Run();
    }
}