using flea_market_service.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

public class ScheduledTasksService
{
    protected readonly IConfiguration Configuration;
    private Timer timer;

    internal ScheduledTasksService(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public void TaskStart()
    {
        // Запускаем таймер, который срабатывает сразу после старта, затем каждые 12 часов
        timer = new Timer(async _ => await ExecuteScheduledTasks(), null, TimeSpan.Zero, TimeSpan.FromHours(12));
    }

    private async Task ExecuteScheduledTasks()
    {
        try
        {
            var offerService = new OfferService(Configuration);
            await offerService.DeleteExpiredOffers();

            var tagService = new TagService(Configuration);
            await tagService.DeleteLowScoreTags();

            Debug.WriteLine($"\nЗапланированные задачи выполнены успешно: {DateTime.Now}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"\nОшибка выполнения запланированных задач: {ex.Message}");
        }
    }

    public void Stop()
    {
        timer?.Dispose();
    }
}