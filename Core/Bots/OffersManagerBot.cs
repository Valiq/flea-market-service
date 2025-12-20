using flea_market_service.Core.Services;
using flea_market_service.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static flea_market_service.Records.Requests;

namespace flea_market_service.Core.Bots
{
    public class OffersManagerBot : IUpdateHandler
    {
        protected readonly IConfiguration Configuration;
        private readonly TelegramBotClient botClient;

        private readonly SellerService sellerService;
        private readonly OfferImageService imageService;
        private readonly ModerationService moderationService;
        private readonly OfferService offerService;
        private readonly TagService tagService;

        private readonly Dictionary<long, AdCreationState> userStates = new();
        private readonly Dictionary<long, UserSessionState> userSessions = new();

        internal OffersManagerBot(IConfiguration configuration)
        {
            Configuration = configuration;

            sellerService = new SellerService(Configuration);
            imageService = new OfferImageService(Configuration);
            moderationService = new ModerationService(Configuration);
            offerService = new OfferService(Configuration);
            tagService = new TagService(Configuration);

            var botToken = Configuration.GetValue<string>("ManagerTelegramBotToken");
            if (string.IsNullOrEmpty(botToken))
            {
                throw new ArgumentException("Telegram bot token is not configured");
            }

            botClient = new TelegramBotClient(botToken);
        }

        #region Bot Lifecycle
        public async Task StartBotAsync(CancellationToken cancellationToken = default)
        {
            var receiverOptions = new ReceiverOptions()
            {
                AllowedUpdates = Array.Empty<UpdateType>(),
                DropPendingUpdates = true,
            };

            botClient.StartReceiving(
                updateHandler: this,
                receiverOptions: receiverOptions,
                cancellationToken: cancellationToken
            );

            Debug.WriteLine("\nBot started in polling mode");
        }

        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Type == UpdateType.Message)
                {
                    if (update.Message?.Text != null)
                    {
                        await HandleMessageAsync(update.Message, cancellationToken);
                    }
                    else if (update.Message?.Photo != null)
                    {
                        await HandlePhotoMessageAsync(update.Message, cancellationToken);
                    }
                }
                else if (update.Type == UpdateType.CallbackQuery)
                {
                    await HandleCallbackQueryAsync(update.CallbackQuery!, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError handling update: {ex.Message}");
            }
        }

        public async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException =>
                    $"Telegram API Error: [{apiRequestException.ErrorCode}] {apiRequestException.Message}",
                _ => exception.ToString()
            };

            Debug.WriteLine($"\nPolling error occurred: {errorMessage}");
            await Task.CompletedTask;
        }

        public async Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
        {
            Debug.WriteLine($"\nError from {source}: {exception.Message}");
            await Task.CompletedTask;
        }

        public async Task StopBotAsync(CancellationToken cancellationToken = default)
        {
            Debug.WriteLine("\nBot stopped");
            await Task.CompletedTask;
        }
        #endregion

        #region User Activity Check
        private async Task<bool?> CheckUserActivityAsync(long chatId)
        {
            try
            {
                return await sellerService.CheckSellerIsActive(chatId.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError checking user activity: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> EnsureUserIsActiveAsync(long chatId, CancellationToken cancellationToken)
        {
            var isActive = await CheckUserActivityAsync(chatId);

            if (isActive == false)
            {
                await SendMessageAsync(chatId, "❌ Сервис недоступен. Вы были заблокированы администрацией.", cancellationToken);
                return false;
            }

            return true;
        }
        #endregion

        #region Message Handlers
        private async Task HandleMessageAsync(Message message, CancellationToken cancellationToken)
        {
            var chatId = message.Chat.Id;
            var messageText = message.Text!;

            if (!await EnsureUserIsActiveAsync(chatId, cancellationToken))
                return;

            if (userStates.ContainsKey(chatId))
            {
                await HandleAdCreationInputAsync(chatId, messageText, cancellationToken);
                return;
            }

            if (userSessions.ContainsKey(chatId) && userSessions[chatId].CurrentState == UserState.SearchingTags)
            {
                await HandleTagSearchAsync(chatId, messageText, cancellationToken);
                return;
            }

            if (messageText == "/start")
            {
                await SendWelcomeMessageAsync(chatId, cancellationToken);
            }
            else
            {
                await SendMainMenuAsync(chatId, "Выберите действие:", cancellationToken);
            }
        }

        private async Task HandlePhotoMessageAsync(Message message, CancellationToken cancellationToken)
        {
            var chatId = message.Chat.Id;

            if (!await EnsureUserIsActiveAsync(chatId, cancellationToken))
                return;

            if (userStates.ContainsKey(chatId))
            {
                await HandlePhotoInputAsync(message, cancellationToken);
            }
        }
        #endregion

        #region Callback Query Handlers
        private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var data = callbackQuery.Data;

            if (string.IsNullOrEmpty(data))
                return;

            if (!await EnsureUserIsActiveAsync(chatId, cancellationToken))
                return;

            if (data.StartsWith("ad_"))
            {
                await HandleAdCreationCallbackAsync(chatId, data, cancellationToken);
            }
            else if (data.StartsWith("offer_"))
            {
                await HandleOfferManagementCallbackAsync(chatId, data, cancellationToken);
            }
            else if (data.StartsWith("search_"))
            {
                await HandleTagSearchCallbackAsync(chatId, data, cancellationToken);
            }
            else
            {
                await HandleMainMenuCallbackAsync(chatId, data, cancellationToken);
            }

            await botClient.AnswerCallbackQuery(
                callbackQueryId: callbackQuery.Id,
                cancellationToken: cancellationToken);
        }

        private async Task HandleMainMenuCallbackAsync(long chatId, string data, CancellationToken cancellationToken)
        {
            if (!await EnsureUserIsActiveAsync(chatId, cancellationToken))
                return;

            switch (data)
            {
                case "push_all":
                    await PushAllUserOffersAsync(chatId, cancellationToken);
                    break;
                case "create_ad":
                    await StartAdCreationAsync(chatId, cancellationToken);
                    break;
                case "my_ads":
                    await ShowUserOffersAsync(chatId, cancellationToken);
                    break;
                case "search_tags":
                    await StartTagSearchAsync(chatId, cancellationToken);
                    break;
                case "hide_all":
                    await HideAllUserOffersAsync(chatId, cancellationToken);
                    break;
                case "resume_all":
                    await ResumeAllUserOffersAsync(chatId, cancellationToken);
                    break;
                default:
                    await SendMainMenuAsync(chatId, "Выберите действие:", cancellationToken);
                    break;
            }
        }
        #endregion

        #region Ad Creation/Editing Logic
        private async Task StartAdCreationAsync(long chatId, CancellationToken cancellationToken, long? offerId = null)
        {
            userStates[chatId] = new AdCreationState
            {
                CurrentStep = AdCreationStep.WaitingForTitle,
                UserId = chatId,
                OfferId = offerId,
                IsEditing = offerId.HasValue
            };

            // Если редактируем существующее объявление - загружаем текущие данные
            if (offerId.HasValue)
            {
                await LoadExistingOfferDataAsync(chatId, offerId.Value, cancellationToken);
            }
            else
            {
                await SendAdCreationStepMessageAsync(chatId, cancellationToken);
            }
        }

        private async Task LoadExistingOfferDataAsync(long chatId, long offerId, CancellationToken cancellationToken)
        {
            try
            {
                var offer = await GetOfferById(offerId);
                if (offer != null)
                {
                    var state = userStates[chatId];
                    state.Title = offer.name ?? "";
                    state.Description = offer.description;
                    state.Price = (double)offer.price;
                    state.Tags = offer.tags?.Select(t => t.name).ToList() ?? new List<string>();
                    state.ExistingImage = offer.image; // Сохраняем существующее изображение

                    // Пропускаем шаги и сразу переходим к просмотру
                    state.CurrentStep = AdCreationStep.Review;
                    await SendReviewMessageAsync(chatId, state, cancellationToken);
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Не удалось загрузить данные объявления.", cancellationToken);
                    userStates.Remove(chatId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError loading offer data: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Ошибка при загрузке данных объявления.", cancellationToken);
                userStates.Remove(chatId);
            }
        }

        private async Task SendAdCreationStepMessageAsync(long chatId, CancellationToken cancellationToken)
        {
            var state = userStates[chatId];

            // Если мы в режиме редактирования отдельного поля - показываем запрос на ввод этого поля
            if (state.EditingField != EditingField.None)
            {
                string message = "";
                InlineKeyboardMarkup keyboard = null;

                switch (state.EditingField)
                {
                    case EditingField.Title:
                        message = "📝 Введите новое название объявления:";
                        break;
                    case EditingField.Description:
                        message = "📄 Введите новое описание объявления (можно пропустить отправив '-'):";
                        break;
                    case EditingField.Tags:
                        message = "🏷️ Введите новые теги через пробел или запятую:";
                        break;
                    case EditingField.Photo:
                        message = "🖼️ Загрузите новое фото или используйте кнопки ниже:";
                        keyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("🗑️ Удалить фото", "ad_remove_photo") },
                            new[] { InlineKeyboardButton.WithCallbackData("❌ Отменить изменение", "ad_cancel_edit") }
                        });
                        break;
                    case EditingField.Price:
                        message = "💰 Введите новую цену:";
                        break;
                }

                if (keyboard == null)
                {
                    keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("❌ Отменить изменение", "ad_cancel_edit") }
                    });
                }

                await botClient.SendMessage(
                    chatId: chatId,
                    text: message,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
                return;
            }

            // Стандартный процесс создания/редактирования
            var standardMessage = state.CurrentStep switch
            {
                AdCreationStep.WaitingForTitle => "📝 Введите название объявления (обязательно):",
                AdCreationStep.WaitingForDescription => "📄 Введите описание объявления (необязательно, можно пропустить отправив '-'):",
                AdCreationStep.WaitingForTags => "🏷️ Введите теги через пробел или запятую (минимум 1 тег, минимальная длина 2 символа):",
                AdCreationStep.WaitingForPhoto => "🖼️ Загрузите фото (не более 3 МБ) или отправьте '-' чтобы пропустить:",
                AdCreationStep.WaitingForPrice => "💰 Введите цену в бел. рублях:",
                AdCreationStep.Review => await GetReviewMessageAsync(state),
                _ => ""
            };

            if (state.CurrentStep == AdCreationStep.Review)
            {
                await SendReviewMessageAsync(chatId, state, cancellationToken);
            }
            else
            {
                var cancelButtonText = state.IsEditing ? "❌ Отменить изменение" : "❌ Отменить создание";
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData(cancelButtonText, "ad_cancel") }
                });

                await botClient.SendMessage(
                    chatId: chatId,
                    text: standardMessage,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
        }

        private async Task HandleAdCreationInputAsync(long chatId, string input, CancellationToken cancellationToken)
        {
            var state = userStates[chatId];

            // Если это редактирование отдельного поля
            if (state.EditingField != EditingField.None)
            {
                await HandleSingleFieldEditAsync(chatId, input, state, cancellationToken);
                return;
            }

            // Стандартный процесс создания
            switch (state.CurrentStep)
            {
                case AdCreationStep.WaitingForTitle:
                    var cleanedTitle = CleanInput(input);
                    if (string.IsNullOrWhiteSpace(cleanedTitle))
                    {
                        await SendMessageAsync(chatId, "❌ Название не может быть пустым. Попробуйте снова:", cancellationToken);
                        return;
                    }
                    state.Title = cleanedTitle;
                    state.CurrentStep = AdCreationStep.WaitingForDescription;
                    break;

                case AdCreationStep.WaitingForDescription:
                    if (input == "-")
                    {
                        state.Description = null;
                    }
                    else
                    {
                        state.Description = CleanInput(input);
                    }
                    state.CurrentStep = AdCreationStep.WaitingForTags;
                    break;

                case AdCreationStep.WaitingForTags:
                    var tags = ParseTags(input);
                    if (tags.Count == 0)
                    {
                        await SendMessageAsync(chatId, "❌ Необходимо ввести хотя бы один тег (мин. 2 символа). Попробуйте снова:", cancellationToken);
                        return;
                    }
                    state.Tags = tags;
                    state.CurrentStep = AdCreationStep.WaitingForPhoto;
                    break;

                case AdCreationStep.WaitingForPhoto:
                    if (input == "-")
                    {
                        state.PhotoFileId = null;
                        state.RemovePhoto = false;
                    }
                    state.CurrentStep = AdCreationStep.WaitingForPrice;
                    break;

                case AdCreationStep.WaitingForPrice:
                    if (!double.TryParse(input.Replace(",", "."), out double price) || price <= 0)
                    {
                        await SendMessageAsync(chatId, "❌ Неверный формат цены. Введите число больше 0:", cancellationToken);
                        return;
                    }
                    state.Price = price;
                    state.CurrentStep = AdCreationStep.Review;
                    break;
            }

            await SendAdCreationStepMessageAsync(chatId, cancellationToken);
        }

        private async Task HandleSingleFieldEditAsync(long chatId, string input, AdCreationState state, CancellationToken cancellationToken)
        {
            var fieldEdited = false;

            switch (state.EditingField)
            {
                case EditingField.Title:
                    var cleanedTitle = CleanInput(input);
                    if (string.IsNullOrWhiteSpace(cleanedTitle))
                    {
                        await SendMessageAsync(chatId, "❌ Название не может быть пустым. Попробуйте снова:", cancellationToken);
                        return;
                    }
                    state.Title = cleanedTitle;
                    fieldEdited = true;
                    break;

                case EditingField.Description:
                    if (input == "-")
                    {
                        state.Description = null;
                    }
                    else
                    {
                        state.Description = CleanInput(input);
                    }
                    fieldEdited = true;
                    break;

                case EditingField.Tags:
                    var tags = ParseTags(input);
                    if (tags.Count == 0)
                    {
                        await SendMessageAsync(chatId, "❌ Необходимо ввести хотя бы один тег (мин. 2 символа). Попробуйте снова:", cancellationToken);
                        return;
                    }
                    state.Tags = tags;
                    fieldEdited = true;
                    break;

                case EditingField.Price:
                    if (!double.TryParse(input.Replace(",", "."), out double price) || price <= 0)
                    {
                        await SendMessageAsync(chatId, "❌ Неверный формат цены. Введите число больше 0:", cancellationToken);
                        return;
                    }
                    state.Price = price;
                    fieldEdited = true;
                    break;
            }

            if (fieldEdited)
            {
                // Сбрасываем поле редактирования и возвращаем к просмотру
                state.EditingField = EditingField.None;
                state.CurrentStep = AdCreationStep.Review;
                await SendReviewMessageAsync(chatId, state, cancellationToken);
            }
        }

        private async Task HandlePhotoInputAsync(Message message, CancellationToken cancellationToken)
        {
            var chatId = message.Chat.Id;
            var state = userStates[chatId];

            var photo = message.Photo!.Last();
            if (photo.FileSize > 3 * 1024 * 1024)
            {
                await SendMessageAsync(chatId, "❌ Фото слишком большое (макс. 3 МБ). Попробуйте снова:", cancellationToken);
                return;
            }

            state.PhotoFileId = photo.FileId;
            state.RemovePhoto = false; // Сбрасываем флаг удаления при загрузке нового фото

            // Если это редактирование отдельного поля - возвращаем к просмотру
            if (state.EditingField == EditingField.Photo)
            {
                state.EditingField = EditingField.None;
                state.CurrentStep = AdCreationStep.Review;
                await SendReviewMessageAsync(chatId, state, cancellationToken);
            }
            else
            {
                state.CurrentStep = AdCreationStep.WaitingForPrice;
                await SendAdCreationStepMessageAsync(chatId, cancellationToken);
            }
        }

        private async Task HandleAdCreationCallbackAsync(long chatId, string callbackData, CancellationToken cancellationToken)
        {
            var state = userStates[chatId];

            switch (callbackData)
            {
                case "ad_cancel":
                    // Полная отмена создания/редактирования
                    userStates.Remove(chatId);
                    var cancelMessage = state.IsEditing ? "Изменение объявления отменено." : "Создание объявления отменено.";

                    if (userSessions.ContainsKey(chatId) && userSessions[chatId].CurrentState == UserState.ViewingOffers)
                    {
                        await ShowUserOffersAsync(chatId, cancellationToken);
                    }
                    else
                    {
                        await SendMainMenuAsync(chatId, cancelMessage, cancellationToken);
                    }
                    return;

                case "ad_cancel_edit":
                    // Отмена редактирования отдельного поля - просто возвращаем к просмотру
                    state.EditingField = EditingField.None;
                    state.CurrentStep = AdCreationStep.Review;
                    await SendReviewMessageAsync(chatId, state, cancellationToken);
                    return;

                case "ad_remove_photo":
                    // Удаление фото при редактировании
                    state.PhotoFileId = null;
                    state.RemovePhoto = true;
                    state.EditingField = EditingField.None;
                    state.CurrentStep = AdCreationStep.Review;
                    await SendReviewMessageAsync(chatId, state, cancellationToken);
                    return;

                case "ad_confirm":
                    await ConfirmAdCreationAsync(chatId, state, cancellationToken);
                    return;

                case "ad_edit_title":
                    state.EditingField = EditingField.Title;
                    await SendAdCreationStepMessageAsync(chatId, cancellationToken);
                    break;

                case "ad_edit_description":
                    state.EditingField = EditingField.Description;
                    await SendAdCreationStepMessageAsync(chatId, cancellationToken);
                    break;

                case "ad_edit_tags":
                    state.EditingField = EditingField.Tags;
                    await SendAdCreationStepMessageAsync(chatId, cancellationToken);
                    break;

                case "ad_edit_photo":
                    state.EditingField = EditingField.Photo;
                    await SendAdCreationStepMessageAsync(chatId, cancellationToken);
                    break;

                case "ad_edit_price":
                    state.EditingField = EditingField.Price;
                    await SendAdCreationStepMessageAsync(chatId, cancellationToken);
                    break;
            }
        }

        private async Task SendReviewMessageAsync(long chatId, AdCreationState state, CancellationToken cancellationToken)
        {
            var reviewMessage = await GetReviewMessageAsync(state);

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "ad_confirm"),
                    InlineKeyboardButton.WithCallbackData("❌ Отменить", "ad_cancel")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✏️ Изменить название", "ad_edit_title"),
                    InlineKeyboardButton.WithCallbackData("✏️ Изменить описание", "ad_edit_description")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✏️ Изменить теги", "ad_edit_tags"),
                    InlineKeyboardButton.WithCallbackData("✏️ Изменить фото", "ad_edit_photo")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✏️ Изменить цену", "ad_edit_price")
                }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: reviewMessage,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task<string> GetReviewMessageAsync(AdCreationState state)
        {
            var action = state.IsEditing ? "изменения" : "создания";
            var message = $"📋 Предварительный просмотр объявления ({action}):\n\n";
            message += $"📌 Название: {state.Title}\n";
            message += $"📄 Описание: {(string.IsNullOrEmpty(state.Description) ? "не указано" : state.Description)}\n";
            message += $"🏷️ Теги: {string.Join(", ", state.Tags)}\n";

            // Улучшенное отображение статуса фото
            if (state.RemovePhoto)
            {
                message += $"🖼️ Фото: будет удалено\n";
            }
            else if (state.PhotoFileId != null)
            {
                message += $"🖼️ Фото: новое фото загружено\n";
            }
            else if (state.ExistingImage != null)
            {
                message += $"🖼️ Фото: текущее фото сохранено\n";
            }
            else
            {
                message += $"🖼️ Фото: не загружено\n";
            }

            message += $"💰 Цена: {state.Price} бел. руб.\n\n";
            message += "Проверьте информацию и подтвердите:";

            return message;
        }

        private async Task ConfirmAdCreationAsync(long chatId, AdCreationState state, CancellationToken cancellationToken)
        {
            try
            {
                long? imageId = null;

                // Логика обработки изображений
                if (state.RemovePhoto)
                {
                    // Пользователь хочет удалить фото
                    imageId = null;
                }
                else if (state.PhotoFileId != null)
                {
                    // Пользователь загрузил новое фото
                    var file = await botClient.GetFile(state.PhotoFileId, cancellationToken);
                    using var stream = new MemoryStream();
                    await botClient.DownloadFile(file.FilePath!, stream, cancellationToken);
                    stream.Position = 0;
                   
                    imageId = await imageService.SaveTelegramImage(stream, state.UserId, "photo.jpg");
                }
                else if (state.IsEditing && state.ExistingImage != null && !state.RemovePhoto)
                {
                    // При редактировании сохраняем существующее фото, если не было загружено новое и не запрошено удаление
                    // Здесь нужно получить imageId из существующего изображения
                    // Предполагаем, что у ExistingImage есть свойство Id
                    imageId = state.ExistingImage.id;
                }

                // Автоматически добавляем теги из названия (только при создании)
                if (!state.IsEditing)
                {
                    var titleTags = ExtractTagsFromTitle(state.Title);
                    foreach (var tag in titleTags)
                    {
                        if (!state.Tags.Contains(tag))
                        {
                            state.Tags.Add(tag);
                        }
                    }
                }

                var sellerName = await GetSellerUsernameAsync(state.UserId);

                var request = new ModerationCreateRequest(
                    name: state.Title,
                    description: state.Description,
                    actionType: state.IsEditing ? "UPDATE" : "CREATE",
                    sellerId: state.UserId.ToString(),
                    chatId: chatId.ToString(),
                    chatType: "MANAGER",
                    sellerName: sellerName,
                    price: state.Price,
                    tags: state.Tags.ToArray(),
                    imageId: imageId,
                    offerId: state.OfferId
                );

                var result = await moderationService.CreateModeration(request);

                if (result > 0)
                {
                    await SendMessageAsync(chatId, "✅ Успешно. Ваше объявление отправлено на модерацию.", cancellationToken);
                }
                else if (result == -2)
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при изменении объявления. Объявление не может быть отправлено на модерацию, т.к. ещё не закончилась модерация по предыдущему запросу.", cancellationToken);
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при создании/изменении объявления. Попробуйте позже.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError creating ad: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Произошла ошибка при создании/изменении объявления.", cancellationToken);
            }
            finally
            {
                userStates.Remove(chatId);
                if (userSessions.ContainsKey(chatId) && userSessions[chatId].CurrentState == UserState.ViewingOffers)
                {
                    await ShowUserOffersAsync(chatId, cancellationToken);
                }
                else
                {
                    await SendMainMenuAsync(chatId, "Возврат в главное меню:", cancellationToken);
                }
            }
        }

        private string CleanInput(string input)
        {
            return Regex.Replace(input.Trim(), @"[^\w\s\.\,\-\!\\?\(\)]", "");
        }

        private List<string> ExtractTagsFromTitle(string title)
        {
            return title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                       .Select(word => word.Trim().ToLower())
                       .Where(word => word.Length > 3)
                       .Select(word => new string(word.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()))
                       .Where(word => !string.IsNullOrEmpty(word))
                       .Distinct()
                       .ToList();
        }

        private List<string> ParseTags(string input)
        {
            return input.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                       .Select(tag => tag.Trim().ToLower())
                       .Where(tag => tag.Length >= 2)
                       .Select(tag => new string(tag.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()))
                       .Where(tag => !string.IsNullOrEmpty(tag))
                       .ToList();
        }
        #endregion

        #region Offer Management Logic
        private async Task ShowUserOffersAsync(long chatId, CancellationToken cancellationToken)
        {
            try
            {
                var sellerId = await GetSellerIdByTelegramId(chatId);
                if (sellerId == null)
                {
                    await SendMessageAsync(chatId, "❌ Продавец не найден. У вас нет никаких объявлений.", cancellationToken);
                    return;
                }

                var offers = await GetSellerOffers(sellerId.Value);
                if (offers == null || !offers.Any())
                {
                    await SendMessageAsync(chatId, "📭 У вас пока нет объявлений.", cancellationToken);
                    return;
                }

                userSessions[chatId] = new UserSessionState
                {
                    CurrentState = UserState.ViewingOffers,
                    UserOffers = offers
                };

                await SendOffersListAsync(chatId, offers, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError getting user offers: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Ошибка при получении списка объявлений.", cancellationToken);
            }
        }

        private async Task SendOffersListAsync(long chatId, List<Offer> offers, CancellationToken cancellationToken)
        {
            var message = "📋 Ваши объявления:\n\n";
            for (int i = 0; i < offers.Count; i++)
            {
                Offer offer = offers[i];
                string statusEmoji = GetStatusEmoji(offer.status.code);

                string lastModerationStatus = string.Empty;

                if (offer.lastModerationStatus is not null)
                    if (offer.lastModerationStatus.Equals("PENDING") || offer.lastModerationStatus.Equals("ON_MODERATION"))
                        lastModerationStatus = $"({GetStatusEmoji("ON_MODERATION")} Новая версия на Модерации)";
                    
                message += $"{i + 1}. {offer.name} {statusEmoji}{offer.status.name.ToUpper()} - {offer.price} руб. {lastModerationStatus}\n";
            }

            var keyboard = new InlineKeyboardMarkup(
                offers.Select((offer, index) => new[]
                {
            InlineKeyboardButton.WithCallbackData(
                $"{index + 1}. {offer.name}",
                $"offer_details_{offer.id}")
                })
                .Concat(new[]
                {
            new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") }
                })
            );

            await botClient.SendMessage(
                chatId: chatId,
                text: message,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private string GetStatusEmoji(string statusCode)
        {
            return statusCode?.ToUpper() switch
            {
                "PUBLISHED" => "✅", 
                "PENDING" => "⏳",  
                "ON_MODERATION" => "👀",
                "DELETED" => "❌",
                "DRAFT" => "📝",
                "HIDDEN" => "🙈",   
                "SUSPENDED" => "⏸️", 
                _ => "📄" 
            };
        }

        private async Task ShowOfferDetailsAsync(long chatId, long offerId, CancellationToken cancellationToken)
        {
            try
            {
                var offer = await GetOfferById(offerId);
                if (offer == null)
                {
                    await SendMessageAsync(chatId, "❌ Объявление не найдено.", cancellationToken);
                    return;
                }

                // Обновляем данные в сессии
                if (userSessions.ContainsKey(chatId))
                {
                    var session = userSessions[chatId];
                    var existingOfferIndex = session.UserOffers?.FindIndex(o => o.id == offerId) ?? -1;
                    if (existingOfferIndex >= 0 && session.UserOffers != null)
                    {
                        session.UserOffers[existingOfferIndex] = offer;
                    }
                }

                // Проверяем наличие изображения в самом объявлении
                if (offer.image != null)
                {
                    try
                    {
                        var imagePath = Path.Combine(offer.image.path, $"{offer.image.name}.{offer.image.extension}");
                        if (File.Exists(imagePath))
                        {
                            await using var stream = System.IO.File.OpenRead(imagePath);
                            await botClient.SendPhoto(
                                chatId: chatId,
                                photo: InputFile.FromStream(stream),
                                caption: await GetOfferDetailsMessage(offer),
                                replyMarkup: GetOfferDetailsKeyboard(offer),
                                cancellationToken: cancellationToken);
                            return;
                        }
                        else
                        {
                            Debug.WriteLine($"\nImage file not found: {imagePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"\nError sending photo: {ex.Message}");
                        // Продолжаем отправлять текстовое сообщение если фото не удалось отправить
                    }
                }

                await botClient.SendMessage(
                    chatId: chatId,
                    text: await GetOfferDetailsMessage(offer),
                    replyMarkup: GetOfferDetailsKeyboard(offer),
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError in ShowOfferDetailsAsync: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Ошибка при загрузке деталей объявления.", cancellationToken);
            }
        }

        private async Task<string> GetOfferDetailsMessage(Offer offer)
        {
            var message = "📄 Детали объявления:\n\n";
            message += $"📌 Название: {offer.name}\n";
            message += $"📄 Описание: {(string.IsNullOrEmpty(offer.description) ? "не указано" : offer.description)}\n";
            message += $"💰 Цена: {offer.price} руб.\n";
            message += $"🏷️ Теги: {(offer.tags != null ? string.Join(", ", offer.tags.Select(t => t.name)) : "нет")}\n";
            message += $"📊 Статус: {offer.status.name}\n";
            message += $"📅 Создано: {FormatDateTime(offer.createAt)}\n";

            return message;
        }

        private string FormatDateTime(string dateTimeString)
        {
            try
            {
                if (DateTime.TryParse(dateTimeString, out DateTime dateTime))
                {
                    return dateTime.ToString("dd.MM.yyyy HH:mm:ss");
                }

                // Если парсинг не удался, попробуем парсить ISO формат
                if (DateTime.TryParseExact(dateTimeString,
                    new[] { "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ssZ" },
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out dateTime))
                {
                    return dateTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
                }

                // Если ничего не получилось, возвращаем оригинальную строку
                return dateTimeString;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError formatting datetime: {ex.Message}");
                return dateTimeString;
            }
        }

        private InlineKeyboardMarkup GetOfferDetailsKeyboard(Offer offer)
        {
            var buttons = new List<InlineKeyboardButton[]>();

            if (offer.status.code == "PUBLISHED")
            {
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("👁️ Скрыть", $"offer_hide_{offer.id}") });
            }
            else if (offer.status.code == "HIDDEN")
            {
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🔔 Возобновить", $"offer_resume_{offer.id}") });
            }

            buttons.AddRange(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✏️ Изменить", $"offer_edit_{offer.id}"),
                    InlineKeyboardButton.WithCallbackData("🗑️ Удалить", $"offer_delete_{offer.id}")
                },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад к списку", "my_ads") }
            });

            return new InlineKeyboardMarkup(buttons);
        }

        private async Task HandleOfferManagementCallbackAsync(long chatId, string callbackData, CancellationToken cancellationToken)
        {
            // Обработка кнопки "Назад к списку"
            if (callbackData == "my_ads")
            {
                await ShowUserOffersAsync(chatId, cancellationToken);
                return;
            }

            var parts = callbackData.Split('_');
            if (parts.Length < 3) return;

            var action = parts[1];
            var offerId = long.Parse(parts[2]);

            switch (action)
            {
                case "details":
                    await ShowOfferDetailsAsync(chatId, offerId, cancellationToken);
                    break;
                case "hide":
                    await UpdateOfferStatusAsync(chatId, offerId, "HIDDEN", "скрыто", cancellationToken);
                    break;
                case "resume":
                    await UpdateOfferStatusAsync(chatId, offerId, "PUBLISHED", "возобновлено", cancellationToken);
                    break;
                case "edit":
                    await ShowEditMenuAsync(chatId, offerId, cancellationToken);
                    break;
                case "delete":
                    await ShowDeleteConfirmationAsync(chatId, offerId, cancellationToken);
                    break;
                case "confirmDelete":
                    await UpdateOfferStatusAsync(chatId, offerId, "DELETED", "удалено", cancellationToken);
                    break;
                case "field" when parts.Length == 4:
                    var field = parts[3];
                    await StartFieldEditingAsync(chatId, offerId, field, cancellationToken);
                    break;
            }
        }

        private async Task ShowEditMenuAsync(long chatId, long offerId, CancellationToken cancellationToken)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить название", $"offer_field_{offerId}_title") },
                new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить описание", $"offer_field_{offerId}_description") },
                new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить теги", $"offer_field_{offerId}_tags") },
                new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить фото", $"offer_field_{offerId}_photo") },
                new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить цену", $"offer_field_{offerId}_price") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", $"offer_details_{offerId}") }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: "📝 Выберите что вы хотите изменить:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task StartFieldEditingAsync(long chatId, long offerId, string field, CancellationToken cancellationToken)
        {
            var editingField = field switch
            {
                "title" => EditingField.Title,
                "description" => EditingField.Description,
                "tags" => EditingField.Tags,
                "photo" => EditingField.Photo,
                "price" => EditingField.Price,
                _ => EditingField.None
            };

            if (editingField == EditingField.None)
            {
                await SendMessageAsync(chatId, "❌ Неверное поле для редактирования.", cancellationToken);
                return;
            }

            // Загружаем текущие данные объявления
            var offer = await GetOfferById(offerId);
            if (offer == null)
            {
                await SendMessageAsync(chatId, "❌ Объявление не найдено.", cancellationToken);
                return;
            }

            userStates[chatId] = new AdCreationState
            {
                CurrentStep = AdCreationStep.Review,
                UserId = chatId,
                OfferId = offerId,
                IsEditing = true,
                EditingField = editingField,
                Title = offer.name ?? "",
                Description = offer.description,
                Price = (double)offer.price,
                Tags = offer.tags?.Select(t => t.name).ToList() ?? new List<string>(),
                ExistingImage = offer.image // Сохраняем существующее изображение
            };

            await SendAdCreationStepMessageAsync(chatId, cancellationToken);
        }

        private async Task ShowDeleteConfirmationAsync(long chatId, long offerId, CancellationToken cancellationToken)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Да", $"offer_confirmDelete_{offerId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Нет", $"offer_details_{offerId}")
                }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: "❓ Вы действительно хотите удалить своё объявление?",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task UpdateOfferStatusAsync(long chatId, long offerId, string status, string statusText, CancellationToken cancellationToken)
        {
            try
            {
                var request = new OfferStatusRequest(
                    status: status,
                    ids: new List<long> { offerId }
                );

                var result = await UpdateOffersStatus(request);
                if (result != null && result.Any())
                {
                    await SendMessageAsync(chatId, $"✅ Ваше объявление успешно {statusText}.", cancellationToken);
                    await ShowUserOffersAsync(chatId, cancellationToken);
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при обновлении статуса объявления.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError updating offer status: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Произошла ошибка при обновлении статуса.", cancellationToken);
            }
        }
        #endregion

        #region Tag Search Logic
        private async Task StartTagSearchAsync(long chatId, CancellationToken cancellationToken)
        {
            userSessions[chatId] = new UserSessionState
            {
                CurrentState = UserState.SearchingTags
            };

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "search_back") }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: "🔍 Введите название тега для поиска:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task HandleTagSearchAsync(long chatId, string searchText, CancellationToken cancellationToken)
        {
            try
            {
                var tags = await GetTagsByPopularity(searchText);
                await SendTagSearchResultsAsync(chatId, tags, searchText, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError searching tags: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Ошибка при поиске тегов.", cancellationToken);
            }
        }

        private async Task SendTagSearchResultsAsync(long chatId, List<Tag> tags, string searchText, CancellationToken cancellationToken)
        {
            var message = tags.Any()
                ? $"🔍 Найдено тегов по запросу '{searchText}':\n\n{string.Join("\n", tags.Select(t => $"• {t.name}"))}"
                : $"😔 По запросу '{searchText}' тегов не найдено.";

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "search_back") }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: message,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task HandleTagSearchCallbackAsync(long chatId, string callbackData, CancellationToken cancellationToken)
        {
            switch (callbackData)
            {
                case "search_tags":
                    await StartTagSearchAsync(chatId, cancellationToken);
                    break;
                case "search_back":
                    userSessions.Remove(chatId);
                    await SendMainMenuAsync(chatId, "Возврат в главное меню:", cancellationToken);
                    break;
            }
        }
        #endregion

        #region Bulk Operations
        private async Task PushAllUserOffersAsync(long chatId, CancellationToken cancellationToken)
        {
            try
            {
                var sellerId = await GetSellerIdByTelegramId(chatId);
                if (sellerId == null)
                {
                    await SendMessageAsync(chatId, "❌ Продавец не найден. У вас нет никаких объявлений.", cancellationToken);
                    return;
                }

                var offers = await GetSellerOffers(sellerId.Value);
                if (offers == null || !offers.Any())
                {
                    await SendMessageAsync(chatId, "📭 У вас пока нет объявлений для пуша.", cancellationToken);
                    return;
                }

                await SendMessageAsync(chatId, "🚀 Инициирован процесс пуша объявлений, позже пришлю отчёт.", cancellationToken);

                var offerIds = offers.Select(o => o.id).ToArray();

                var result = await offerService.PushOffersWithNotifications(offerIds);

                if (result is not null)
                {
                    await SendMessageAsync(chatId, $"✅ Статус пуша объявлений:\n\n{string.Join("\n", result)}", cancellationToken);
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при пуше объявлений.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError pushing offers: {ex.Message}");

                if (ex is InvalidOperationException)
                    await SendMessageAsync(chatId, $"⚠️ {ex.Message}", cancellationToken);
                else
                    await SendMessageAsync(chatId, "❌ Произошла ошибка при пуше объявлений.", cancellationToken);
            }
        }

        private async Task HideAllUserOffersAsync(long chatId, CancellationToken cancellationToken)
        {
            await ProcessBulkOfferOperation(chatId, "HIDDEN", "скрыты", cancellationToken);
        }

        private async Task ResumeAllUserOffersAsync(long chatId, CancellationToken cancellationToken)
        {
            await ProcessBulkOfferOperation(chatId, "PUBLISHED", "возобновлены", cancellationToken);
        }

        private async Task ProcessBulkOfferOperation(long chatId, string status, string statusText, CancellationToken cancellationToken)
        {
            try
            {
                var sellerId = await GetSellerIdByTelegramId(chatId);
                if (sellerId == null)
                {
                    await SendMessageAsync(chatId, "❌ Продавец не найден. У вас нет никаких объявлений.", cancellationToken);
                    return;
                }

                var offers = await GetSellerOffers(sellerId.Value);
                if (offers == null || !offers.Any())
                {
                    await SendMessageAsync(chatId, "📭 У вас пока нет объявлений.", cancellationToken);
                    return;
                }

                var request = new OfferStatusRequest(
                    status: status,
                    ids: offers.Select(o => o.id).ToList()
                );

                var result = await UpdateOffersStatus(request);
                if (result != null && result.Any())
                {
                    await SendMessageAsync(chatId, $"✅ Ваши объявления успешно {statusText}.", cancellationToken);
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при обновлении статуса объявлений.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError in bulk operation: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Произошла ошибка при выполнении операции.", cancellationToken);
            }
        }
        #endregion

        #region Service Methods
        private async Task<long?> GetSellerIdByTelegramId(long telegramId)
        {
            var request = new SellerRequest(
                telegramId: telegramId,
                name: default,
                isActive: default,
                createAtStartDate: default,
                createAtEndDate: default,
                sortBy: default,
                sortType: default,
                pageNumber: 1,
                pageSize: 1
            );

            var response = await sellerService.GetSellersPaged(request, string.Empty, true);
            return response.sellers?.FirstOrDefault()?.id;
        }

        private async Task<List<Offer>> GetSellerOffers(long sellerId)
        {
            var request = new OfferRequest(
                sellerId: sellerId,
                name: default,
                sellerName: default,
                startPrice: default,
                endPrice: default,
                status: [ "PUBLISHED", "HIDDEN", "SUSPENDED"],
                tags: default,
                lastPushingStartDate: default,
                lastPushingEndDate: default,
                createAtStartDate: default,
                createAtEndDate: default,
                changeAtStartDate: default,
                changeAtEndDate: default,
                sortBy: "createdate",
                sortType: "DESC",
                pageNumber: 1,
                pageSize: 50
            );

            var response = await offerService.GetOffersPaged(request, string.Empty, true);
            return response?.offers;
        }

        private async Task<Offer?> GetOfferById(long offerId)
        {
            try
            {
                return await offerService.GetOffer(offerId, null, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError getting offer by id: {ex.Message}");
                return null;
            }
        }

        private async Task<List<long>> UpdateOffersStatus(OfferStatusRequest request)
        {
            return await offerService.UpdateOffersStatus(request, string.Empty, true);
        }

        private async Task<List<Tag>> GetTagsByPopularity(string tagName)
        {
            return await tagService.GetTagsByPopularity(tagName, 25);
        }

        private async Task<string> GetSellerUsernameAsync(long telegramId)
        {
            try
            {
                var user = await botClient.GetChat(telegramId);
                var username = !string.IsNullOrEmpty(user.Username)
                    ? $"@{user.Username}"
                    : !string.IsNullOrEmpty(user.FirstName)
                        ? user.FirstName
                        : $"user_{telegramId}";

                return username;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError getting seller username: {ex.Message}");
                return $"user_{telegramId}";
            }
        }
        #endregion

        #region Utility Methods
        private async Task SendWelcomeMessageAsync(long chatId, CancellationToken cancellationToken)
        {
            var isActive = await CheckUserActivityAsync(chatId);
            if (isActive == false)
            {
                await SendMessageAsync(chatId, "❌ Сервис недоступен. Вы были заблокированы администрацией.", cancellationToken);
                return;
            }

            var welcomeText = "👋 Добро пожаловать в Flea Market Bot!\n\n" +
                             "Здесь вы можете создавать и управлять своими объявлениями.";

            await SendMainMenuAsync(chatId, welcomeText, cancellationToken);
        }

        private async Task SendMainMenuAsync(long chatId, string message, CancellationToken cancellationToken)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🚀 Запушить все объявления", "push_all")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("📝 Создать объявление", "create_ad"),
                    InlineKeyboardButton.WithCallbackData("📋 Список моих объявлений", "my_ads")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔍 Поиск тегов", "search_tags")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("👁️ Скрыть все объявления", "hide_all"),
                    InlineKeyboardButton.WithCallbackData("🔔 Возобновить все объявления", "resume_all")
                }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: message,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task SendMessageAsync(long chatId, string message, CancellationToken cancellationToken)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: message,
                cancellationToken: cancellationToken);
        }

        public async Task<bool> SendMessageToSeller(long chatId, string messageText)
        {
            try
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: $"🔔 {messageText}");

                Debug.WriteLine($"\nMessage successfully sent to seller chat {chatId}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError sending message to seller chat {chatId}: {ex.Message}");
                return false;
            }
        }
        #endregion
    }

    #region Supporting Classes and Enums
    public class AdCreationState
    {
        public AdCreationStep CurrentStep { get; set; }
        public long UserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<string> Tags { get; set; } = new();
        public string? PhotoFileId { get; set; }
        public double Price { get; set; }
        public long? OfferId { get; set; }
        public bool IsEditing { get; set; }
        public EditingField EditingField { get; set; }
        public OfferImage? ExistingImage { get; set; } // Сохраняем существующее изображение
        public bool RemovePhoto { get; set; } // Флаг для удаления фото
    }

    public enum AdCreationStep
    {
        WaitingForTitle,
        WaitingForDescription,
        WaitingForTags,
        WaitingForPhoto,
        WaitingForPrice,
        Review
    }

    public enum EditingField
    {
        None,
        Title,
        Description,
        Tags,
        Photo,
        Price
    }

    public class UserSessionState
    {
        public UserState CurrentState { get; set; }
        public List<Offer>? UserOffers { get; set; }
    }

    public enum UserState
    {
        MainMenu,
        ViewingOffers,
        SearchingTags
    }
    #endregion
}