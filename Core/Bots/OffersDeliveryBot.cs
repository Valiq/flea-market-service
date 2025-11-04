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
using static flea_market_service.Records.Respons;

namespace flea_market_service.Core.Bots
{
    public class OffersDeliveryBot : IUpdateHandler
    {
        protected readonly IConfiguration Configuration;
        private readonly TelegramBotClient botClient;
        private readonly SellerService sellerService;
        private readonly FilterService filterService;
        private readonly OfferService offerService;
        private readonly TagService tagService;
        private readonly Dictionary<long, UserSessionState> userSessions = new();
        private readonly Dictionary<long, FilterCreationState> filterStates = new();
        private readonly Dictionary<long, FilterEditingState> filterEditingStates = new();

        internal OffersDeliveryBot(IConfiguration configuration)
        {
            Configuration = configuration;
            sellerService = new SellerService(Configuration);
            filterService = new FilterService(Configuration);
            offerService = new OfferService(Configuration);
            tagService = new TagService(Configuration);

            var botToken = Configuration.GetValue<string>("DeliveryTelegramBotToken");
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

            Debug.WriteLine("\nDelivery bot started in polling mode");
        }

        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Type == UpdateType.Message && update.Message?.Text != null)
                {
                    await HandleMessageAsync(update.Message, cancellationToken);
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
            Debug.WriteLine("\nDelivery bot stopped");
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

            // Обработка создания фильтра
            if (filterStates.ContainsKey(chatId))
            {
                await HandleFilterCreationInputAsync(chatId, messageText, cancellationToken);
                return;
            }

            // Обработка редактирования фильтра
            if (filterEditingStates.ContainsKey(chatId))
            {
                await HandleFilterEditingInputAsync(chatId, messageText, cancellationToken);
                return;
            }

            // Обработка поиска тегов
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

            if (data.Equals("search_filters"))
            {
                await SendMessageAsync(chatId, "🔍 Выбран пункт: Найти по активным фильтрам", cancellationToken);
                await StartSearchByFiltersAsync(chatId, cancellationToken);
            }
            else if (data.Equals("search_all"))
            {
                await SendMessageAsync(chatId, "📋 Выбран пункт: Найти всё подряд", cancellationToken);
                await StartSearchAllAsync(chatId, cancellationToken);
            }
            else if (data.Equals("search_tags"))
            {
                await StartTagSearchAsync(chatId, cancellationToken);
            }
            else if (data.Equals("manage_filters"))
            {
                await ShowFilterManagementMenuAsync(chatId, cancellationToken);
            }
            else if (data.StartsWith("search_"))
            {
                await HandleSearchCallbackAsync(chatId, data, cancellationToken);
            }
            else if (data.StartsWith("filter_"))
            {
                await HandleFilterCallbackAsync(chatId, data, cancellationToken);
            }
            else if (data.StartsWith("offer_"))
            {
                await HandleOfferNavigationCallbackAsync(chatId, data, cancellationToken);
            }
            else if (data.StartsWith("edit_filter_"))
            {
                await HandleFilterEditCallbackAsync(chatId, data, cancellationToken);
            }
            else
            {
                await SendMainMenuAsync(chatId, "Выберите действие:", cancellationToken);
            }

            await botClient.AnswerCallbackQuery(
                callbackQueryId: callbackQuery.Id,
                cancellationToken: cancellationToken);
        }
        #endregion

        #region Main Menu
        private async Task SendWelcomeMessageAsync(long chatId, CancellationToken cancellationToken)
        {
            var isActive = await CheckUserActivityAsync(chatId);
            if (isActive == false)
            {
                await SendMessageAsync(chatId, "❌ Сервис недоступен. Вы были заблокированы администрацией.", cancellationToken);
                return;
            }

            var welcomeText = "👋 Добро пожаловать в Flea Market Delivery Bot!\n\n" +
                             "Здесь вы можете искать объявления по фильтрам.";

            await SendMainMenuAsync(chatId, welcomeText, cancellationToken);
        }

        private async Task SendMainMenuAsync(long chatId, string message, CancellationToken cancellationToken)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔍 Найти по активным фильтрам", "search_filters")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("📋 Найти всё подряд", "search_all")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🏷️ Поиск тегов", "search_tags")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⚙️ Управление фильтрами", "manage_filters")
                }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: message,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        #endregion

        #region Search Logic
        private async Task StartSearchByFiltersAsync(long chatId, CancellationToken cancellationToken)
        {
            await ShowSortOptionsMenuAsync(chatId, "search_filters", cancellationToken);
        }

        private async Task StartSearchAllAsync(long chatId, CancellationToken cancellationToken)
        {
            await ShowSortOptionsMenuAsync(chatId, "search_all", cancellationToken);
        }

        private async Task ShowSortOptionsMenuAsync(long chatId, string searchType, CancellationToken cancellationToken)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("📅 Новые сначала", $"{searchType}_sort_createdate_desc"),
                    InlineKeyboardButton.WithCallbackData("📅 Старые сначала", $"{searchType}_sort_createdate_asc")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("💰 Дорогие сначала", $"{searchType}_sort_price_desc"),
                    InlineKeyboardButton.WithCallbackData("💰 Дешёвые сначала", $"{searchType}_sort_price_asc")
                },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: "📊 Выберите способ сортировки результатов:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task HandleSearchCallbackAsync(long chatId, string callbackData, CancellationToken cancellationToken)
        {
            var parts = callbackData.Split("_sort_");
            var searchType = parts[0];

            var partForSort = parts[1].Split('_');
            if (partForSort.Length != 2) return;

            var sortBy = partForSort[0];
            var sortType = partForSort[1];

            if (searchType == "search_filters")
            {
                await StartFilteredSearchAsync(chatId, sortBy, sortType, 1, cancellationToken);
            }
            else if (searchType == "search_all")
            {
                await StartAllSearchAsync(chatId, sortBy, sortType, 1, cancellationToken);
            }
        }

        private async Task StartFilteredSearchAsync(long chatId, string sortBy, string sortType, int pageNumber, CancellationToken cancellationToken)
        {
            try
            {
                var filters = await filterService.GetSellerFilters(chatId.ToString());
                var filterIds = filters?.Where(f => f.isActive).Select(f => f.id).ToArray() ?? Array.Empty<long>();

                if (!filterIds.Any())
                {
                    await SendMessageAsync(chatId, "❌ У вас нет активных фильтров.", cancellationToken);
                    await SendMainMenuAsync(chatId, "Создайте фильтры в меню управления:", cancellationToken);
                    return;
                }

                var response = await filterService.GetOffersByFiltersPaged(filterIds, pageNumber, 1, sortBy, sortType);
                await ShowSearchResultAsync(chatId, response, searchType: "filters", sortBy, sortType, pageNumber, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError in filtered search: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Ошибка при поиске по фильтрам.", cancellationToken);
            }
        }

        private async Task StartAllSearchAsync(long chatId, string sortBy, string sortType, int pageNumber, CancellationToken cancellationToken)
        {
            try
            {
                // Корректируем названия полей для сортировки согласно спецификации
                var correctedSortBy = sortBy == "createdate" ? "Create_At" : "Price";
                var correctedSortType = sortType.ToUpper();

                var request = new OfferRequest(
                    sellerId: default,
                    name: default,
                    sellerName: default,
                    startPrice: default,
                    endPrice: default,
                    status: new[] { "PUBLISHED" },
                    tags: default,
                    lastPushingStartDate: default,
                    lastPushingEndDate: default,
                    createAtStartDate: default,
                    createAtEndDate: default,
                    changeAtStartDate: default,
                    changeAtEndDate: default,
                    sortBy: correctedSortBy,
                    sortType: correctedSortType,
                    pageNumber: pageNumber,
                    pageSize: 1
                );

                var response = await offerService.GetOffersPaged(request, string.Empty, true);
                await ShowSearchResultAsync(chatId, response, searchType: "all", sortBy, sortType, pageNumber, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError in all search: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Ошибка при поиске объявлений.", cancellationToken);
            }
        }

        private async Task ShowSearchResultAsync(long chatId, OffersResponse response, string searchType, string sortBy, string sortType, int pageNumber, CancellationToken cancellationToken)
        {
            if (response?.offers == null || !response.offers.Any())
            {
                await SendMessageAsync(chatId, "😔 Объявления не найдены.", cancellationToken);
                await SendMainMenuAsync(chatId, "Попробуйте изменить параметры поиска:", cancellationToken);
                return;
            }

            var offer = response.offers.First();
            await SendOfferCardAsync(chatId, offer, pageNumber, response.totalPages, cancellationToken);

            // Сохраняем состояние для навигации
            userSessions[chatId] = new UserSessionState
            {
                CurrentState = UserState.ViewingSearchResults,
                SearchType = searchType,
                SortBy = sortBy,
                SortType = sortType,
                CurrentPage = pageNumber,
                TotalPages = response.totalPages
            };

            // Кнопки навигации
            var keyboardButtons = new List<InlineKeyboardButton[]>();

            if (pageNumber < response.totalPages)
            {
                keyboardButtons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData("➡️ Далее", $"offer_next_{searchType}_{sortBy}_{sortType}_{pageNumber + 1}")
                });
            }

            keyboardButtons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("❌ Отмена", "main_menu")
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: "Выберите действие:",
                replyMarkup: new InlineKeyboardMarkup(keyboardButtons),
                cancellationToken: cancellationToken);
        }

        private async Task HandleOfferNavigationCallbackAsync(long chatId, string callbackData, CancellationToken cancellationToken)
        {
            var parts = callbackData.Split('_');
            if (parts.Length < 6) return;

            var action = parts[1];
            var searchType = parts[2];
            var sortBy = parts[3];
            var sortType = parts[4];
            var pageNumber = int.Parse(parts[5]);

            if (action == "next")
            {
                if (searchType == "filters")
                {
                    await StartFilteredSearchAsync(chatId, sortBy, sortType, pageNumber, cancellationToken);
                }
                else if (searchType == "all")
                {
                    await StartAllSearchAsync(chatId, sortBy, sortType, pageNumber, cancellationToken);
                }
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
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") }
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
                var tags = await tagService.GetTagsByPopularity(searchText, 25);
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
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: message,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        #endregion

        #region Filter Management
        private async Task ShowFilterManagementMenuAsync(long chatId, CancellationToken cancellationToken)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("➕ Создать фильтр", "filter_create") },
                new[] { InlineKeyboardButton.WithCallbackData("📋 Список моих фильтров", "filter_list") },
                new[] { InlineKeyboardButton.WithCallbackData("🔔 Активировать все фильтры", "filter_activate_all") },
                new[] { InlineKeyboardButton.WithCallbackData("👁️ Отключить все фильтры", "filter_deactivate_all") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: "⚙️ Управление фильтрами:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task HandleFilterCallbackAsync(long chatId, string callbackData, CancellationToken cancellationToken)
        {
            var parts = callbackData.Split('_');
            if (parts.Length < 2) return;

            switch (callbackData)
            {
                case "filter_create":
                    await StartFilterCreationAsync(chatId, cancellationToken);
                    break;
                case "filter_list":
                    await ShowUserFiltersAsync(chatId, cancellationToken);
                    break;
                case "filter_activate_all":
                    await ActivateAllFiltersAsync(chatId, cancellationToken);
                    break;
                case "filter_deactivate_all":
                    await DeactivateAllFiltersAsync(chatId, cancellationToken);
                    break;
                case "filter_cancel":
                    filterStates.Remove(chatId);
                    await ShowUserFiltersAsync(chatId, cancellationToken); // Возвращаем к списку фильтров
                    break;
                case "filter_confirm":
                    await ConfirmFilterCreationAsync(chatId, cancellationToken);
                    break;
                case "filter_edit_name":
                    await StartFilterFieldEditingAsync(chatId, EditingField.Name, cancellationToken);
                    break;
                case "filter_edit_tags":
                    await StartFilterFieldEditingAsync(chatId, EditingField.Tags, cancellationToken);
                    break;
                case "filter_cancel_edit":
                    if (filterStates.ContainsKey(chatId))
                    {
                        filterStates[chatId].EditingField = EditingField.None;
                        await SendFilterReviewMessageAsync(chatId, filterStates[chatId], cancellationToken);
                    }
                    break;
                default:
                    if (parts[1] == "details" && parts.Length == 3)
                    {
                        var filterId = long.Parse(parts[2]);
                        await ShowFilterDetailsAsync(chatId, filterId, cancellationToken);
                    }
                    else if (parts[1] == "activate" && parts.Length == 3)
                    {
                        var filterId = long.Parse(parts[2]);
                        await UpdateFilterStatusAsync(chatId, filterId, true, cancellationToken);
                    }
                    else if (parts[1] == "deactivate" && parts.Length == 3)
                    {
                        var filterId = long.Parse(parts[2]);
                        await UpdateFilterStatusAsync(chatId, filterId, false, cancellationToken);
                    }
                    else if (parts[1] == "delete" && parts.Length == 3)
                    {
                        var filterId = long.Parse(parts[2]);
                        await ShowDeleteConfirmationAsync(chatId, filterId, cancellationToken);
                    }
                    else if (parts[1] == "confirm" && parts[2] == "delete" && parts.Length == 4)
                    {
                        var filterId = long.Parse(parts[3]);
                        await DeleteFilterAsync(chatId, filterId, cancellationToken);
                    }
                    else if (parts[1] == "edit" && parts.Length == 3)
                    {
                        var filterId = long.Parse(parts[2]);
                        await StartFilterEditingAsync(chatId, filterId, cancellationToken);
                    }
                    break;
            }
        }

        #region Filter Creation
        private async Task StartFilterCreationAsync(long chatId, CancellationToken cancellationToken)
        {
            filterStates[chatId] = new FilterCreationState
            {
                CurrentStep = FilterCreationStep.WaitingForName,
                UserId = chatId
            };

            await SendFilterCreationStepMessageAsync(chatId, cancellationToken);
        }

        private async Task HandleFilterCreationInputAsync(long chatId, string input, CancellationToken cancellationToken)
        {
            var state = filterStates[chatId];

            // Если редактируем отдельное поле
            if (state.EditingField != EditingField.None)
            {
                await HandleFilterFieldEditAsync(chatId, input, state, cancellationToken);
                return;
            }

            // Стандартный процесс создания
            switch (state.CurrentStep)
            {
                case FilterCreationStep.WaitingForName:
                    var cleanedName = CleanInput(input);
                    if (string.IsNullOrWhiteSpace(cleanedName))
                    {
                        await SendMessageAsync(chatId, "❌ Название не может быть пустым. Попробуйте снова:", cancellationToken);
                        return;
                    }
                    state.Name = cleanedName;
                    state.CurrentStep = FilterCreationStep.WaitingForTags;
                    break;

                case FilterCreationStep.WaitingForTags:
                    var tags = ParseTags(input);
                    if (tags.Count < 1)
                    {
                        await SendMessageAsync(chatId, "❌ Необходимо ввести минимум 1 тег. Попробуйте снова:", cancellationToken);
                        return;
                    }
                    state.Tags = tags;
                    state.CurrentStep = FilterCreationStep.Review;
                    break;
            }

            await SendFilterCreationStepMessageAsync(chatId, cancellationToken);
        }

        private async Task HandleFilterFieldEditAsync(long chatId, string input, FilterCreationState state, CancellationToken cancellationToken)
        {
            var fieldEdited = false;

            switch (state.EditingField)
            {
                case EditingField.Name:
                    var cleanedName = CleanInput(input);
                    if (string.IsNullOrWhiteSpace(cleanedName))
                    {
                        await SendMessageAsync(chatId, "❌ Название не может быть пустым. Попробуйте снова:", cancellationToken);
                        return;
                    }
                    state.Name = cleanedName;
                    fieldEdited = true;
                    break;

                case EditingField.Tags:
                    var tags = ParseTags(input);
                    if (tags.Count < 1)
                    {
                        await SendMessageAsync(chatId, "❌ Необходимо ввести минимум 1 тег. Попробуйте снова:", cancellationToken);
                        return;
                    }
                    state.Tags = tags;
                    fieldEdited = true;
                    break;
            }

            if (fieldEdited)
            {
                state.EditingField = EditingField.None;
                state.CurrentStep = FilterCreationStep.Review;
                await SendFilterReviewMessageAsync(chatId, state, cancellationToken);
            }
        }

        private async Task SendFilterCreationStepMessageAsync(long chatId, CancellationToken cancellationToken)
        {
            var state = filterStates[chatId];

            // Если редактируем отдельное поле
            if (state.EditingField != EditingField.None)
            {
                string message = state.EditingField switch
                {
                    EditingField.Name => "📝 Введите новое название фильтра:",
                    EditingField.Tags => "🏷️ Введите новые теги через пробел или запятую (минимум 1 тег, минимальная длина тега - 2 символа):",
                    _ => ""
                };

                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("❌ Отменить изменение", "filter_cancel_edit") }
                });

                await botClient.SendMessage(
                    chatId: chatId,
                    text: message,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
                return;
            }

            // Стандартный процесс
            var standardMessage = state.CurrentStep switch
            {
                FilterCreationStep.WaitingForName => "📝 Введите название фильтра (обязательно):",
                FilterCreationStep.WaitingForTags => "🏷️ Введите теги через пробел или запятую (минимум 1 тег, минимальная длина тега - 2 символа):",
                FilterCreationStep.Review => GetFilterReviewMessage(state),
                _ => ""
            };

            if (state.CurrentStep == FilterCreationStep.Review)
            {
                await SendFilterReviewMessageAsync(chatId, state, cancellationToken);
            }
            else
            {
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("❌ Отменить создание", "filter_cancel") }
                });

                await botClient.SendMessage(
                    chatId: chatId,
                    text: standardMessage,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
        }

        private async Task SendFilterReviewMessageAsync(long chatId, FilterCreationState state, CancellationToken cancellationToken)
        {
            var reviewMessage = GetFilterReviewMessage(state);

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Создать", "filter_confirm"),
                    InlineKeyboardButton.WithCallbackData("❌ Отменить", "filter_cancel")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✏️ Изменить название", "filter_edit_name"),
                    InlineKeyboardButton.WithCallbackData("✏️ Изменить теги", "filter_edit_tags")
                }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: reviewMessage,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private string GetFilterReviewMessage(FilterCreationState state)
        {
            var message = $"📋 Предварительный просмотр фильтра:\n\n";
            message += $"📌 Название: {state.Name}\n";
            message += $"🏷️ Теги: {string.Join(", ", state.Tags)}\n\n";
            message += "Проверьте информацию и подтвердите создание:";

            return message;
        }

        private async Task StartFilterFieldEditingAsync(long chatId, EditingField field, CancellationToken cancellationToken)
        {
            var state = filterStates[chatId];
            state.EditingField = field;
            await SendFilterCreationStepMessageAsync(chatId, cancellationToken);
        }

        private async Task ConfirmFilterCreationAsync(long chatId, CancellationToken cancellationToken)
        {
            var state = filterStates[chatId];
            var sellerName = await GetSellerUsernameAsync(state.UserId);

            try
            {
                var request = new FilterCreateRequest(
                    sellerId: state.UserId.ToString(),
                    chatId: chatId.ToString(),
                    chatType: "DELIVERY",
                    sellerName: sellerName,
                    name: state.Name,
                    tags: state.Tags.ToArray()
                );

                var result = await filterService.CreateFilter(request);

                if (result > 0)
                {
                    await SendMessageAsync(chatId, "✅ Ваш фильтр успешно создан.", cancellationToken);
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при создании фильтра.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError creating filter: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Произошла ошибка при создании фильтра.", cancellationToken);
            }
            finally
            {
                filterStates.Remove(chatId);
                await ShowUserFiltersAsync(chatId, cancellationToken); // Возвращаем к списку фильтров
            }
        }
        #endregion

        #region Filter List and Management
        private async Task ShowUserFiltersAsync(long chatId, CancellationToken cancellationToken)
        {
            try
            {
                var filters = await filterService.GetSellerFilters(chatId.ToString());
                if (filters == null || !filters.Any())
                {
                    await SendMessageAsync(chatId, "📭 У вас пока нет фильтров.", cancellationToken);
                    return;
                }

                var message = "📋 Ваши фильтры:\n\n";
                for (int i = 0; i < filters.Count; i++)
                {
                    var filter = filters[i];
                    var statusEmoji = filter.isActive ? "✅" : "❌";
                    message += $"{i + 1}. {statusEmoji} {filter.name}\n";
                }

                var keyboardButtons = new List<InlineKeyboardButton[]>();

                // Создаем кнопки с полными названиями фильтров
                int index = 0;
                foreach (var filter in filters)
                {
                    var buttonText = $"{index + 1}. {filter.name}";
                    keyboardButtons.Add(new[]
                    {
                        InlineKeyboardButton.WithCallbackData(buttonText, $"filter_details_{filter.id}")
                    });
                    index++;
                }

                keyboardButtons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔙 Назад", "manage_filters")
                });

                await botClient.SendMessage(
                    chatId: chatId,
                    text: message,
                    replyMarkup: new InlineKeyboardMarkup(keyboardButtons),
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError getting user filters: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Ошибка при получении списка фильтров.", cancellationToken);
            }
        }

        private async Task ShowFilterDetailsAsync(long chatId, long filterId, CancellationToken cancellationToken)
        {
            try
            {
                var filter = await filterService.GetFilterById(filterId);
                if (filter == null)
                {
                    await SendMessageAsync(chatId, "❌ Фильтр не найден.", cancellationToken);
                    return;
                }

                var message = $"📋 Детали фильтра:\n\n";
                message += $"📌 Название: {filter.name}\n";
                message += $"🏷️ Теги: {(filter.tags.Any() ? string.Join(", ", filter.tags.Select(x => x.name)) : "не указаны")}\n";
                message += $"📊 Статус: {(filter.isActive ? "✅ Активен" : "❌ Неактивен")}\n";
                message += $"📅 Создан: {FormatDateTime(filter.createAt)}\n";

                var keyboardButtons = new List<InlineKeyboardButton[]>();

                if (filter.isActive)
                {
                    keyboardButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("👁️ Отключить", $"filter_deactivate_{filterId}") });
                }
                else
                {
                    keyboardButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("🔔 Включить", $"filter_activate_{filterId}") });
                }

                keyboardButtons.AddRange(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✏️ Редактировать", $"filter_edit_{filterId}"),
                        InlineKeyboardButton.WithCallbackData("🗑️ Удалить", $"filter_delete_{filterId}")
                    },
                    new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад к списку", "filter_list") }
                });

                await botClient.SendMessage(
                    chatId: chatId,
                    text: message,
                    replyMarkup: new InlineKeyboardMarkup(keyboardButtons),
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError showing filter details: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Ошибка при загрузке деталей фильтра.", cancellationToken);
            }
        }

        private async Task UpdateFilterStatusAsync(long chatId, long filterId, bool isActive, CancellationToken cancellationToken)
        {
            try
            {
                var result = await filterService.UpdateFilterIsActive(new[] { filterId }, isActive);
                if (result)
                {
                    var message = isActive ? "✅ Фильтр успешно активирован." : "👁️ Фильтр успешно отключен.";
                    await SendMessageAsync(chatId, message, cancellationToken);
                    // Не выводим повторно меню управления фильтрами
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при обновлении статуса фильтра.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError updating filter status: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Произошла ошибка при обновлении статуса.", cancellationToken);
            }
        }

        private async Task ShowDeleteConfirmationAsync(long chatId, long filterId, CancellationToken cancellationToken)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Да", $"filter_confirm_delete_{filterId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Нет", $"filter_details_{filterId}")
                }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: "❓ Вы действительно хотите удалить этот фильтр?",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task DeleteFilterAsync(long chatId, long filterId, CancellationToken cancellationToken)
        {
            try
            {
                var result = await filterService.DeleteFilter(filterId);
                if (result)
                {
                    await SendMessageAsync(chatId, "✅ Фильтр успешно удалён.", cancellationToken);
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Фильтр не найден, возможно он уже удалён.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError deleting filter: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Произошла ошибка при удалении фильтра.", cancellationToken);
            }
            finally
            {
                await ShowUserFiltersAsync(chatId, cancellationToken);
            }
        }
        #endregion

        #region Filter Editing
        private async Task StartFilterEditingAsync(long chatId, long filterId, CancellationToken cancellationToken)
        {
            try
            {
                var filter = await filterService.GetFilterById(filterId);
                if (filter == null)
                {
                    await SendMessageAsync(chatId, "❌ Фильтр не найден.", cancellationToken);
                    return;
                }

                filterEditingStates[chatId] = new FilterEditingState
                {
                    FilterId = filterId,
                    CurrentName = filter.name,
                    CurrentTags = new List<string>(), // Здесь нужно получить теги фильтра
                    EditingField = EditingField.None
                };

                await ShowFilterEditMenuAsync(chatId, filterId, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError starting filter editing: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Ошибка при начале редактирования фильтра.", cancellationToken);
            }
        }

        private async Task ShowFilterEditMenuAsync(long chatId, long filterId, CancellationToken cancellationToken)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить название", $"edit_filter_name_{filterId}") },
                new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить теги", $"edit_filter_tags_{filterId}") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", $"filter_details_{filterId}") }
            });

            await botClient.SendMessage(
                chatId: chatId,
                text: "📝 Выберите что вы хотите изменить:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private async Task HandleFilterEditCallbackAsync(long chatId, string callbackData, CancellationToken cancellationToken)
        {
            var parts = callbackData.Split('_');
            if (parts.Length < 4) return;

            var action = parts[2];
            var filterId = long.Parse(parts[3]);

            if (action == "cancel")
            {
                filterEditingStates.Remove(chatId);
                await ShowFilterDetailsAsync(chatId, filterId, cancellationToken);
                return;
            }

            if (!filterEditingStates.ContainsKey(chatId))
            {
                await StartFilterEditingAsync(chatId, filterId, cancellationToken);
                return;
            }

            var state = filterEditingStates[chatId];
            state.EditingField = action switch
            {
                "name" => EditingField.Name,
                "tags" => EditingField.Tags,
                _ => EditingField.None
            };

            if (state.EditingField != EditingField.None)
            {
                string message = state.EditingField switch
                {
                    EditingField.Name => "📝 Введите новое название фильтра:",
                    EditingField.Tags => "🏷️ Введите новые теги через пробел или запятую (минимум 1 тег, минимальная длина тега - 2 символа):",
                    _ => ""
                };

                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("❌ Отменить изменение", $"edit_filter_cancel_{filterId}") }
                });

                await botClient.SendMessage(
                    chatId: chatId,
                    text: message,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
        }

        private async Task HandleFilterEditingInputAsync(long chatId, string input, CancellationToken cancellationToken)
        {
            if (!filterEditingStates.ContainsKey(chatId)) return;

            var state = filterEditingStates[chatId];

            try
            {
                FilterUpdateRequest request = state.EditingField switch
                {
                    EditingField.Name => new FilterUpdateRequest(name: CleanInput(input), isActive: null, tags: null),
                    EditingField.Tags => new FilterUpdateRequest(name: null, isActive: null, tags: ParseTags(input).ToArray()),
                    _ => new FilterUpdateRequest(name: null, isActive: null, tags: null)
                };

                var result = await filterService.UpdateFilter(state.FilterId, request);

                if (result)
                {
                    await SendMessageAsync(chatId, "✅ Фильтр успешно обновлён.", cancellationToken);

                    // Обновляем состояние
                    if (state.EditingField == EditingField.Name)
                    {
                        state.CurrentName = CleanInput(input);
                    }

                    state.EditingField = EditingField.None;
                    await ShowFilterDetailsAsync(chatId, state.FilterId, cancellationToken);
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при обновлении фильтра.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError updating filter: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Произошла ошибка при обновлении фильтра.", cancellationToken);
            }
            finally
            {
                if (state.EditingField != EditingField.None)
                {
                    state.EditingField = EditingField.None;
                }
            }
        }
        #endregion

        #region Bulk Filter Operations
        private async Task ActivateAllFiltersAsync(long chatId, CancellationToken cancellationToken)
        {
            try
            {
                var filters = await filterService.GetSellerFilters(chatId.ToString());
                var filterIds = filters?.Select(f => f.id).ToArray() ?? Array.Empty<long>();

                if (!filterIds.Any())
                {
                    await SendMessageAsync(chatId, "📭 У вас нет фильтров для активации.", cancellationToken);
                    return;
                }

                var result = await filterService.UpdateFilterIsActive(filterIds, true);
                if (result)
                {
                    await SendMessageAsync(chatId, "✅ Все ваши фильтры успешно активированы.", cancellationToken);
                    // Не выводим повторно меню управления фильтрами
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при активации фильтров.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError activating filters: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Произошла ошибка при активации фильтров.", cancellationToken);
            }
        }

        private async Task DeactivateAllFiltersAsync(long chatId, CancellationToken cancellationToken)
        {
            try
            {
                var filters = await filterService.GetSellerFilters(chatId.ToString());
                var filterIds = filters?.Select(f => f.id).ToArray() ?? Array.Empty<long>();

                if (!filterIds.Any())
                {
                    await SendMessageAsync(chatId, "📭 У вас нет фильтров для отключения.", cancellationToken);
                    return;
                }

                var result = await filterService.UpdateFilterIsActive(filterIds, false);
                if (result)
                {
                    await SendMessageAsync(chatId, "✅ Все ваши фильтры успешно отключены.", cancellationToken);
                    // Не выводим повторно меню управления фильтрами
                }
                else
                {
                    await SendMessageAsync(chatId, "❌ Ошибка при отключении фильтров.", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError deactivating filters: {ex.Message}");
                await SendMessageAsync(chatId, "❌ Произошла ошибка при отключении фильтров.", cancellationToken);
            }
        }
        #endregion
        #endregion

        #region Offer Delivery Method
        public async Task<bool> SendOfferToSeller(long chatId, long offerId)
        {
            try
            {
                var offer = await offerService.GetOffer(offerId, null, true);
                if (offer == null)
                {
                    Debug.WriteLine($"\nOffer {offerId} not found");
                    return false;
                }

                await SendOfferCardAsync(chatId, offer, 1, 1, new CancellationToken());

                Debug.WriteLine($"\nOffer {offerId} successfully sent to chat {chatId}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nError sending offer to chat {chatId}: {ex.Message}");
                return false;
            }
        }
        #endregion

        #region Utility Methods
        private async Task SendOfferCardAsync(long chatId, Offer offer, int currentPage, int totalPages, CancellationToken cancellationToken)
        {
            var progressText = totalPages > 1 ? $"📊 Просмотр: {currentPage} из {totalPages}\n\n" : "";

            var message = progressText + GetOfferCardText(offer);

            // Попытка отправить фото
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
                            caption: message,
                            cancellationToken: cancellationToken);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"\nError sending photo: {ex.Message}");
                }
            }

            // Если фото не удалось отправить, отправляем текстовое сообщение
            await botClient.SendMessage(
                chatId: chatId,
                text: message,
                cancellationToken: cancellationToken);
        }

        private string GetOfferCardText(Offer offer)
        {
            var message = $"📄 Название: {offer.name}\n\n";
            message += $"📝 Описание: {(string.IsNullOrEmpty(offer.description) ? "не указано" : offer.description)}\n";
            message += $"🏷️ Теги: {(offer.tags != null ? string.Join(", ", offer.tags.Select(t => t.name)) : "нет")}\n";
            message += $"💰 Цена: {offer.price} руб.\n";
            message += $"👤 Продавец: {offer.seller.name}\n";
            message += $"📅 Создано: {FormatDateTime(offer.createAt)}\n";

            return message;
        }

        private string FormatDateTime(string dateTimeString)
        {
            try
            {
                if (DateTime.TryParse(dateTimeString, out DateTime dateTime))
                {
                    return dateTime.ToString("dd.MM.yyyy HH:mm");
                }

                // Парсинг ISO формата
                if (DateTime.TryParseExact(dateTimeString,
                    new[] { "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ssZ" },
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out dateTime))
                {
                    return dateTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                }

                return dateTimeString;
            }
            catch
            {
                return dateTimeString;
            }
        }

        private async Task SendMessageAsync(long chatId, string message, CancellationToken cancellationToken)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: message,
                cancellationToken: cancellationToken);
        }

        private string CleanInput(string input)
        {
            return Regex.Replace(input.Trim(), @"[^\w\s\.\,\-\!\\?\(\)]", "");
        }

        private List<string> ParseTags(string input)
        {
            return input.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(tag => tag.Trim().ToLower())
                       .Where(tag => tag.Length >= 2) // Минимальная длина тега - 2 символа
                       .Select(tag => new string(tag.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()))
                       .Where(tag => !string.IsNullOrEmpty(tag))
                       .Distinct()
                       .ToList();
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

        #region Supporting Classes and Enums
        public class UserSessionState
        {
            public UserState CurrentState { get; set; }
            public string SearchType { get; set; } = string.Empty;
            public string SortBy { get; set; } = string.Empty;
            public string SortType { get; set; } = string.Empty;
            public int CurrentPage { get; set; }
            public int TotalPages { get; set; }
        }

        public class FilterCreationState
        {
            public FilterCreationStep CurrentStep { get; set; }
            public long UserId { get; set; }
            public string Name { get; set; } = string.Empty;
            public List<string> Tags { get; set; } = new();
            public EditingField EditingField { get; set; }
        }

        public class FilterEditingState
        {
            public long FilterId { get; set; }
            public string CurrentName { get; set; } = string.Empty;
            public List<string> CurrentTags { get; set; } = new();
            public EditingField EditingField { get; set; }
        }

        public enum UserState
        {
            MainMenu,
            ViewingSearchResults,
            SearchingTags,
            ManagingFilters
        }

        public enum FilterCreationStep
        {
            WaitingForName,
            WaitingForTags,
            Review
        }

        public enum EditingField
        {
            None,
            Name,
            Tags
        }
        #endregion
    }
}