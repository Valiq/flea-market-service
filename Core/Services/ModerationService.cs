using Dapper;
using flea_market_service.Core.Bots;
using flea_market_service.Models;
using Microsoft.AspNetCore.Authorization;
using MySqlConnector;
using System;
using System.Data.Common;
using System.Diagnostics;
using System.Transactions;
using static flea_market_service.Records.Requests;
using static flea_market_service.Records.Respons;
using static System.Net.Mime.MediaTypeNames;

namespace flea_market_service.Core.Services
{
    public class ModerationService
    {
        protected readonly IConfiguration Configuration;
        private AuthorizationService authorizationService;

        internal ModerationService(IConfiguration configuration)
        {
            Configuration = configuration;
            authorizationService = new AuthorizationService(Configuration);
        }

        private async Task<long> CheckGrants(MySqlConnection connection, string token, string[] roles)
        {
            long checkGrantsResult = await authorizationService.CheckGrants(connection, token, roles);

            if (checkGrantsResult == -1)
                throw new Exception("У вас нет необходимой роли");

            return checkGrantsResult;
        }

        internal async Task<long> CreateModeration(ModerationCreateRequest request)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();
                using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    SellerService service = new SellerService(Configuration);

                    var sellerId = (await service.CreateOrUpdateSeller(request.sellerId, request.chatId, request.chatType, request.sellerName)).id;

                    if (sellerId == null)
                        throw new Exception($"Продавец с Telegram ID {request.sellerId} не найден");

                    var actionTypeSql = "SELECT ID FROM ACTION_TYPE WHERE Code = @ActionType AND Actual = TRUE";
                    var actionTypeId = await connection.ExecuteScalarAsync<long?>(actionTypeSql, new { ActionType = request.actionType }, transaction);

                    var statusSql = "SELECT ID FROM MODERATION_STATUS WHERE Code = 'PENDING' AND Actual = TRUE";
                    var moderationStatusId = await connection.ExecuteScalarAsync<long?>(statusSql, transaction: transaction)
                                          ?? throw new Exception("Статус модерации не найден");

                    if (request.actionType.Equals("UPDATE"))
                    {
                        var lastModerationByOfferSql = @"
                            SELECT m.ID FROM MODERATION as m 
                            JOIN OFFER_AUDIT as oa ON m.ID = oa.ID 
                            WHERE Offer_ID = @OfferId 
                            AND Action_Type_ID = @ActionTypeId 
                            AND Moderation_Status_Id IN ('PENDING', 'ON_MODERATION')
                            ORDER BY m.Create_At DESC 
                            LIMIT 1";

                        var result = await connection.QueryAsync<long>(lastModerationByOfferSql, 
                            new {
                                OfferId = request.offerId,
                                ActionTypeId = actionTypeId
                            }, transaction);

                        if (result.Count() > 0)
                            return -2;
                    }

                    var offerAuditSql = @"
                        INSERT INTO OFFER_AUDIT (
                            Name, Description, Seller_ID, Image_ID, Price, Tags, Offer_ID, Action_Type_ID) 
                        VALUES (
                            @Name, @Description, @SellerId,
                            @ImageId, @Price, @Tags, @OfferId, @ActionTypeId);
                        SELECT LAST_INSERT_ID();";

                    var offerAuditParams = new
                    {
                        Name = request.name,
                        Description = request.description,
                        SellerId = sellerId, // Используем ID из таблицы SELLER
                        ImageId = request.imageId,
                        Price = (decimal)request.price,
                        Tags = request.tags != null ? string.Join(";", request.tags.Select(t => t.Trim())) : null,
                        OfferId = request.offerId,
                        ActionTypeId = actionTypeId
                    };

                    var offerAuditId = await connection.ExecuteScalarAsync<long>(offerAuditSql, offerAuditParams, transaction);

                    var moderationSql = @"
                        INSERT INTO MODERATION (
                            Offer_Audit_ID, Moderation_Status_ID, Comment, Change_At, Change_By) 
                        VALUES (
                            @OfferAuditId, @ModerationStatusId, NULL, NULL, NULL);
                        SELECT LAST_INSERT_ID();";

                    var moderationParams = new
                    {
                        OfferAuditId = offerAuditId,
                        ModerationStatusId = moderationStatusId
                    };

                    var moderationId = await connection.ExecuteScalarAsync<long>(moderationSql, moderationParams, transaction);

                    await transaction.CommitAsync();

                    return moderationId;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    Debug.WriteLine($"\nОшибка при создании модерации: {ex.Message}");
                    return -1;
                }
            }
        }

        internal async Task<ModerationsResponse> GetModerationsPaged(ModerationRequest request, string token)
        {
            string[] roles = { "ADMIN", "MODERATOR", "VIEWER" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                long checkGrantsResult = await CheckGrants(connection, token, roles);

                int offset = (request.pageNumber - 1) * request.pageSize;

                // Динамическое построение WHERE условия
                var whereConditions = new List<string>();
                var parameters = new DynamicParameters();

                // Базовые условия
                whereConditions.Add("ms.Actual = @ModStatusActual");
                parameters.Add("ModStatusActual", true);

                // Фильтрация по статусу модерации
                if (request.ModerationStatus != null && request.ModerationStatus.Length > 0)
                {
                    whereConditions.Add("ms.Code IN @ModerationStatus");
                    parameters.Add("ModerationStatus", request.ModerationStatus);
                }

                // Фильтрация по дате создания модерации (начало)
                if (!string.IsNullOrEmpty(request.createAtStartDate))
                {
                    whereConditions.Add("m.Create_At >= @CreateAtStart");
                    parameters.Add("CreateAtStart", request.createAtStartDate);
                }

                // Фильтрация по дате создания модерации (конец)
                if (!string.IsNullOrEmpty(request.createAtEndDate))
                {
                    whereConditions.Add("m.Create_At <= @CreateAtEnd");
                    parameters.Add("CreateAtEnd", request.createAtEndDate);
                }

                // Фильтрация по дате изменения модерации (начало)
                if (!string.IsNullOrEmpty(request.changeAtStartDate))
                {
                    whereConditions.Add("m.Change_At >= @ChangeAtStart");
                    parameters.Add("ChangeAtStart", request.changeAtStartDate);
                }

                // Фильтрация по дате изменения модерации (конец)
                if (!string.IsNullOrEmpty(request.changeAtEndDate))
                {
                    whereConditions.Add("m.Change_At <= @ChangeAtEnd");
                    parameters.Add("ChangeAtEnd", request.changeAtEndDate);
                }

                // Фильтрация по имени сотрудника
                if (!string.IsNullOrEmpty(request.changeByName))
                {
                    whereConditions.Add("u.Name LIKE @ChangeByName");
                    parameters.Add("ChangeByName", $"%{request.changeByName}%");
                }

                // Фильтрация по названию оффера
                if (!string.IsNullOrEmpty(request.name))
                {
                    whereConditions.Add("oa.Name LIKE @Name");
                    parameters.Add("Name", $"%{request.name}%");
                }

                // Фильтрация по типу действия
                if (request.actionType != null && request.actionType.Length > 0)
                {
                    whereConditions.Add("at.Code IN @ActionType");
                    parameters.Add("ActionType", request.actionType);
                }

                // Фильтрация по продавцу
                if (request.sellerId.HasValue)
                {
                    whereConditions.Add("s.ID = @SellerId");
                    parameters.Add("SellerId", request.sellerId.Value);
                }

                // Фильтрация по имени продавца

                if (!string.IsNullOrEmpty(request.sellerName))
                {
                    whereConditions.Add("s.Name LIKE @SellerName");
                    parameters.Add("SellerName", $"%{request.sellerName}%");
                }

                // Фильтрация по цене (начало)
                if (request.startPrice.HasValue)
                {
                    whereConditions.Add("oa.Price >= @StartPrice");
                    parameters.Add("StartPrice", (decimal)request.startPrice.Value);
                }

                // Фильтрация по цене (конец)
                if (request.endPrice.HasValue)
                {
                    whereConditions.Add("oa.Price <= @EndPrice");
                    parameters.Add("EndPrice", (decimal)request.endPrice.Value);
                }

                // Фильтрация по ID оффера
                if (!string.IsNullOrEmpty(request.offerId) && long.TryParse(request.offerId, out long offerId))
                {
                    whereConditions.Add("oa.Offer_ID = @OfferId");
                    parameters.Add("OfferId", offerId);
                }

                // Фильтрация по тегам
                if (request.tags != null && request.tags.Length > 0)
                {
                    var tagConditions = new List<string>();
                    for (int i = 0; i < request.tags.Length; i++)
                    {
                        var tagParam = $"@Tag{i}";
                        tagConditions.Add($"FIND_IN_SET(@Tag0, REPLACE(oa.Tags, ';', ','))");
                        parameters.Add($"Tag{i}", request.tags[i]);
                    }
                    whereConditions.Add($"({string.Join(" OR ", tagConditions)})");
                }

                string whereClause = whereConditions.Count > 0
                    ? $"WHERE {string.Join(" AND ", whereConditions)}"
                    : "";

                // Валидация и настройка сортировки
                var validSortColumns = new Dictionary<string, string>
                {
                    ["id"] = "m.ID",
                    ["createdate"] = "m.Create_At",
                    ["changedate"] = "m.Change_At",
                    ["status"] = "ms.Code",
                    ["name"] = "oa.Name",
                    ["actiontype"] = "at.Name",
                    ["sellerid"] = "s.ID",
                    ["sellername"] = "s.Name",
                    ["changeby"] = "u.Name",
                    ["price"] = "oa.Price",
                    ["offerid"] = "oa.Offer_ID"
                };

                var sortColumnKey = request.sortBy?.ToLower();
                string sortColumn = "m.Create_At";

                if (sortColumnKey is not null && validSortColumns.ContainsKey(sortColumnKey))
                {
                    sortColumn = validSortColumns[sortColumnKey];
                }

                var orderByClause = $"ORDER BY {sortColumn} {request.sortType ?? "DESC"}";

                // SQL для получения общего количества записей
                var countSql = $@"
                    SELECT COUNT(*)
                    FROM MODERATION m
                    INNER JOIN MODERATION_STATUS ms ON m.Moderation_Status_ID = ms.ID
                    INNER JOIN OFFER_AUDIT oa ON m.Offer_Audit_ID = oa.ID
                    INNER JOIN SELLER s ON oa.Seller_ID = s.ID
                    LEFT JOIN ACTION_TYPE at ON oa.Action_Type_ID = at.ID
                    LEFT JOIN USER u ON m.Change_By = u.ID
            {whereClause}";

                int totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);
                int totalPages = (int)Math.Ceiling(totalCount / (double)request.pageSize);

                // SQL для получения данных с пагинацией
                var sql = $@"
                    SELECT 
                        -- Moderation fields
                        m.ID,
                        m.Comment as Comment,
                        DATE_FORMAT(m.Change_At, '%Y-%m-%dT%H:%i:%s') as ChangeAt, 
                        DATE_FORMAT(m.Create_At, '%Y-%m-%dT%H:%i:%s') as CreateAt, 
                        m.Offer_Audit_ID as OfferAuditId,
                        m.Moderation_Status_ID as ModerationStatusId,
                        m.Change_By as ChangeById,
                        
                        -- ModerationStatus fields
                        ms.ID,
                        ms.Name as Name,
                        ms.Code as Code,
                        ms.Actual as Actual,
                        
                        -- User fields
                        u.ID,
                        u.Name as Name,
                        u.Email as Email,
                        
                        -- OfferAudit fields
                        oa.ID,
                        oa.Name as Name,
                        oa.Description as Description,
                        oa.Price as Price,
                        oa.Tags as Tags,
                        oa.Offer_ID as OfferId,
                        DATE_FORMAT(oa.Create_At, '%Y-%m-%dT%H:%i:%s') as CreateAt,
                        
                        -- Seller fields
                        s.ID,
                        s.Telegram_ID as TelegramId,
                        s.Name as Name,
                        s.Is_Active as IsActive,
                        DATE_FORMAT(s.Create_At, '%Y-%m-%dT%H:%i:%s') as CreateAt,
                        
                        -- OfferImage fields
                        oi.ID,
                        oi.Name as Name,
                        oi.Path as Path,
                        oi.Extension as Extension,
                        oi.Size as Size,
                        DATE_FORMAT(oi.Create_At, '%Y-%m-%dT%H:%i:%s') as CreateAt,
                        
                        -- ActionType fields
                        at.ID,
                        at.Name as Name,
                        at.Code as Code,
                        at.Actual as Actual
                        
                    FROM MODERATION m
                    INNER JOIN MODERATION_STATUS ms ON m.Moderation_Status_ID = ms.ID
                    INNER JOIN OFFER_AUDIT oa ON m.Offer_Audit_ID = oa.ID
                    INNER JOIN SELLER s ON oa.Seller_ID = s.ID
                    LEFT JOIN ACTION_TYPE at ON oa.Action_Type_ID = at.ID
                    LEFT JOIN USER u ON m.Change_By = u.ID
                    LEFT JOIN OFFER_IMAGE oi ON oa.Image_ID = oi.ID
                    {whereClause}
                    {orderByClause}
                    LIMIT @PageSize OFFSET @Offset";

                parameters.Add("PageSize", request.pageSize);
                parameters.Add("Offset", offset);

                // Выполняем объединенный запрос
                var moderationsDict = new Dictionary<long, Moderation>();

                var moderationsData = await connection.QueryAsync<
                    Moderation,
                    ModerationStatus,
                    User,
                    OfferAudit,
                    Seller,
                    OfferImage,
                    ActionType,
                    Moderation>(
                    sql,
                    (moderation, status, user, offerAudit, seller, image, actionType) =>
                    {
                        // Если модерация уже есть в словаре, используем ее
                        if (!moderationsDict.TryGetValue(moderation.id, out var moderationEntry))
                        {
                            moderationEntry = moderation;

                            // Заполняем связанные объекты
                            if (status != null)
                            {
                                moderationEntry.moderationStatus = status;
                            }

                            if (user != null && user.id > 0)
                            {
                                moderationEntry.changeBy = user;
                            }

                            if (offerAudit != null)
                            {
                                moderationEntry.offerAudit = offerAudit;

                                // Заполняем связанные объекты OfferAudit
                                if (seller != null)
                                {
                                    moderationEntry.offerAudit.seller = seller;
                                }

                                if (image != null && image.id > 0)
                                {
                                    moderationEntry.offerAudit.image = image;
                                }

                                if (actionType != null)
                                {
                                    moderationEntry.offerAudit.actionType = actionType;
                                }

                                // Обрабатываем теги
                                if (!string.IsNullOrEmpty(offerAudit.tags))
                                {
                                    moderationEntry.offerAudit.tags = offerAudit.tags;
                                }
                            }

                            moderationsDict.Add(moderationEntry.id, moderationEntry);
                        }

                        return moderationEntry;
                    },
                    parameters,
                    splitOn: "ID"
                );

                var moderations = moderationsDict.Values.ToList();

                return new ModerationsResponse(
                    moderations,
                    totalCount,
                    request.pageNumber,
                    request.pageSize,
                    totalPages
                );
            }
        }

        internal async Task<Moderation> GetModeration(long moderationId, string token)
        {
            string[] roles = {"ADMIN", "MODERATOR", "VIEWER" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                long checkGrantsResult = await CheckGrants(connection, token, roles);

                var sql = @"
                    SELECT 
                        m.ID as moderation_id,
                        m.Comment as moderation_comment,
                        DATE_FORMAT(m.Change_At, '%Y-%m-%dT%H:%i:%s') as moderation_changeAt, 
                        DATE_FORMAT(m.Create_At, '%Y-%m-%dT%H:%i:%s') as moderation_createAt, 
                
                        oa.ID as offerAudit_id,
                        oa.Name as offerAudit_name,
                        oa.Description as offerAudit_description,
                        oa.Price as offerAudit_price,
                        oa.Tags as offerAudit_tags,
                        oa.Offer_ID as offerAudit_offerId,
                        DATE_FORMAT(oa.Create_At, '%Y-%m-%dT%H:%i:%s') as offerAudit_createAt,
                
                        s.ID as seller_id,
                        s.Telegram_ID as seller_telegramId,
                        s.Name as seller_name,
                        s.Is_Active as seller_isActive,
                        DATE_FORMAT(s.Create_At, '%Y-%m-%dT%H:%i:%s') as seller_createAt,
                
                        oi.ID as offerImage_id,
                        oi.Name as offerImage_name,
                        oi.Path as offerImage_path,
                        oi.Extension as offerImage_extension,
                        oi.Size as offerImage_size,
                        DATE_FORMAT(oi.Create_At, '%Y-%m-%dT%H:%i:%s') as offerImage_createAt,
                
                        ms.ID as moderationStatus_id,
                        ms.Name as moderationStatus_name,
                        ms.Code as moderationStatus_code,
                        ms.Actual as moderationStatus_actual,
                
                        u.ID as user_id,
                        u.Name as user_name,
                        u.Email as user_email,
                
                        at.ID as actionType_id,
                        at.Name as actionType_name,
                        at.Code as actionType_code,
                        at.Actual as actionType_actual
                
                    FROM MODERATION m
                    INNER JOIN OFFER_AUDIT oa ON m.Offer_Audit_ID = oa.ID
                    INNER JOIN SELLER s ON oa.Seller_ID = s.ID
                    INNER JOIN MODERATION_STATUS ms ON m.Moderation_Status_ID = ms.ID
                    LEFT JOIN OFFER_IMAGE oi ON oa.Image_ID = oi.ID
                    LEFT JOIN USER u ON m.Change_By = u.ID
                    LEFT JOIN ACTION_TYPE at ON oa.Action_Type_ID = at.ID
                    WHERE m.ID = @ModerationId";

                var result = await connection.QueryFirstOrDefaultAsync(sql, new { ModerationId = moderationId });

                if (result == null)
                    return null;

                return new Moderation
                {
                    id = result.moderation_id,
                    comment = result.moderation_comment,
                    changeAt = result.moderation_changeAt?.ToString(),
                    createAt = result.moderation_createAt.ToString(),
                    offerAudit = new OfferAudit
                    {
                        id = result.offerAudit_id,
                        name = result.offerAudit_name,
                        description = result.offerAudit_description,
                        price = result.offerAudit_price,
                        tags = result.offerAudit_tags,
                        offerId = result.offerAudit_offerId,
                        createAt = result.offerAudit_createAt.ToString(),
                        seller = new Seller
                        {
                            id = result.seller_id,
                            telegramId = result.seller_telegramId,
                            name = result.seller_name,
                            isActive = result.seller_isActive,
                            createAt = result.seller_createAt.ToString()
                        },
                        image = result.offerImage_id != null ? new OfferImage
                        {
                            id = result.offerImage_id,
                            name = result.offerImage_name,
                            path = result.offerImage_path,
                            extension = result.offerImage_extension,
                            size = result.offerImage_size,
                            createAt = result.offerImage_createAt?.ToString()
                        } : null,
                        actionType = new ActionType
                        {
                            id = result.actionType_id,
                            name = result.actionType_name,
                            code = result.actionType_code,
                            actual = result.actionType_actual
                        }
                    },
                    moderationStatus = new ModerationStatus
                    {
                        id = result.moderationStatus_id,
                        name = result.moderationStatus_name,
                        code = result.moderationStatus_code,
                        actual = result.moderationStatus_actual
                    },
                    changeBy = result.user_id != null ? new User
                    {
                        id = result.user_id,
                        name = result.user_name,
                        email = result.user_email
                    } : null
                };
            }
        }

        internal async Task<bool> UpdateModeration(long moderationId, ModerationUpdateRequest request, string token)
        {
            string[] roles = { "ADMIN", "MODERATOR" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                // Проверяем права доступа
                long currentUserId = await CheckGrants(connection, token, roles);

                // Получаем текущую модерацию для проверки статуса и получения offerAuditId
                var currentModeration = await connection.QueryFirstOrDefaultAsync(
                    @"SELECT m.*, ms.Code as StatusCode, m.Offer_Audit_ID as OfferAuditId 
                    FROM MODERATION m 
                    INNER JOIN MODERATION_STATUS ms ON m.Moderation_Status_ID = ms.ID 
                    WHERE m.ID = @ModerationId",
                    new { ModerationId = moderationId });

                if (currentModeration == null)
                    throw new ArgumentException("Модерация не найдена");

                // Проверяем возможность изменения статуса
                if (!string.IsNullOrEmpty(request.status))
                {
                    CheckStatusTransition(currentModeration.StatusCode, request.status.ToUpper());
                }

                long createId = await connection.ExecuteScalarAsync<long>("SELECT ID FROM ACTION_TYPE WHERE Code = 'CREATE'");
                long updateId = await connection.ExecuteScalarAsync<long>("SELECT ID FROM ACTION_TYPE WHERE Code = 'UPDATE'");

                using (var transaction = await connection.BeginTransactionAsync())
                {
                    try
                    {
                        var updateFields = new List<string>();
                        var parameters = new DynamicParameters();
                        parameters.Add("ModerationId", moderationId);

                        // Обновление статуса модерации
                        if (!string.IsNullOrEmpty(request.status))
                        {
                            var statusId = await connection.ExecuteScalarAsync<long>(
                                "SELECT ID FROM MODERATION_STATUS WHERE Code = @Code",
                                new { Code = request.status },
                                transaction);

                            if (statusId == 0)
                            {
                                throw new ArgumentException("Указан неверный статус модерации");
                            }

                            updateFields.Add("Moderation_Status_ID = @StatusId");
                            updateFields.Add("Change_At = @ChangeAt");
                            updateFields.Add("Change_By = @ChangeBy");
                            parameters.Add("StatusId", statusId);
                            parameters.Add("ChangeAt", DateTime.Now);
                            parameters.Add("ChangeBy", currentUserId);
                        }

                        // Обновление комментария
                        if (request.comment is not null)
                        {
                            if (request.comment.Equals(""))
                            {
                                updateFields.Add("Comment = NULL");
                            }
                            else
                            {
                                updateFields.Add("Comment = @Comment");
                                parameters.Add("Comment", request.comment);
                            }
                        }

                        // Если есть что обновлять
                        if (updateFields.Count > 0)
                        {
                            var updateSql = $"UPDATE MODERATION SET {string.Join(", ", updateFields)} WHERE ID = @ModerationId";
                            int rowsAffected = await connection.ExecuteAsync(updateSql, parameters, transaction);

                            if (rowsAffected == 0)
                            {
                                throw new Exception("Не удалось обновить модерацию");
                            }

                            // Получаем OfferAudit для публикации
                            var offerAudit = await connection.QueryFirstOrDefaultAsync<OfferAuditRecord>(
                                @"SELECT 
                                    oa.ID, oa.Name, oa.Description, oa.Image_ID as imageId, oa.Seller_ID as sellerId, oa.Price, oa.Tags, 
                                    oa.Offer_ID as offerId, oa.Action_Type_ID as actionTypeId
                                FROM OFFER_AUDIT oa 
                                WHERE oa.ID = @OfferAuditId",
                                new { OfferAuditId = currentModeration.OfferAuditId },
                                transaction);

                            if (offerAudit == null)
                                throw new Exception("Не найден OfferAudit для публикации");

                            var offerService = new OfferService(Configuration);

                            // Если статус обновляется на "НА ПУБЛИКАЦИЮ"
                            if (!string.IsNullOrEmpty(request.status) && request.status.ToUpper() == "FOR_PUBLICATION")
                            {
                                if (offerAudit.actionTypeId == createId)
                                {
                                    await offerService.CreateOffer(connection, transaction, offerAudit.id);
                                    await NotifySeller(offerAudit.id, connection, transaction, $"Ваш запрос на создание предложения <{offerAudit.name}> был выполнен");
                                }

                                if (offerAudit.actionTypeId == updateId)
                                {
                                    await offerService.UpdateOffer(connection, transaction, offerAudit.id);
                                    await NotifySeller(offerAudit.id, connection, transaction, $"Ваш запрос на обновление предложения <{offerAudit.name}> был выполнен");
                                }
                            }

                            // Если статус обновляется на "ОТКЛОНЕНО"
                            if (!string.IsNullOrEmpty(request.status) && request.status.ToUpper() == "REJECTED")
                            {
                                if (offerAudit.actionTypeId == updateId)
                                {
                                    await offerService.UpdateOffer(connection, transaction, offerAudit.id);
                                }

                                var moderationComment = connection.ExecuteScalar<string>(
                                    @"SELECT comment FROM MODERATION WHERE ID = @ModerationId", 
                                    new { ModerationId = moderationId }, 
                                    transaction);

                                string commentToSeller = string.Empty;
                                var buffer = moderationComment?.Split(">");

                                if(buffer?.Count() > 1)
                                    commentToSeller = buffer[1];

                                await NotifySeller(offerAudit.id, connection, transaction, $"Ваш запрос для предложения <{offerAudit.name}> был отклонён модерацией." +
                                                                                           $"{(!string.IsNullOrEmpty(commentToSeller) ? $"\n\nКомментарий от модерации: {commentToSeller.Trim()}" : string.Empty )}");
                            }
                        }

                        await transaction.CommitAsync();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }
            }
        }

        // Метод проверки переходов статусов
        private void CheckStatusTransition(string currentStatus, string newStatus)
        {
            var allowedTransitions = new Dictionary<string, string[]>
            {
                { "PENDING", new[] { "ON_MODERATION" } },
                { "ON_MODERATION", new[] { "FOR_PUBLICATION", "REJECTED" } },
                { "FOR_PUBLICATION", new string[0] }, // Конечный статус
                { "REJECTED", new string[0] } // Конечный статус
            };

            // Проверяем существование текущего статуса
            if (!allowedTransitions.ContainsKey(currentStatus))
            {
                throw new InvalidOperationException($"Неизвестный текущий статус: {currentStatus}");
            }

            // Проверяем возможность перехода
            var allowedNextStatuses = allowedTransitions[currentStatus];

            if (!allowedNextStatuses.Contains(newStatus))
            {
                throw new InvalidOperationException(
                    $"Невозможен переход из статуса {currentStatus} в статус {newStatus}. " +
                    $"Допустимые переходы: {(allowedNextStatuses.Length > 0 ? string.Join(", ", allowedNextStatuses) : "нет (конечный статус)")}");
            }
        }

        private async Task NotifySeller(long auditId, MySqlConnection connection, MySqlTransaction transaction, string messageText)
        {
            // Получаем sellerId по offerId
            var chatIds = await connection.QueryAsync<long>(
                @"SELECT s.Manager_Chat_Id 
                    FROM SELLER s 
                    INNER JOIN OFFER_AUDIT oa ON s.ID = oa.Seller_ID 
                    WHERE oa.ID = @AuditId 
                    AND s.Is_Active = TRUE"
            ,
                new { AuditId = auditId }, transaction);

            if (!chatIds.Any())
                throw new ArgumentException("Не найдено активных продавцов для данного предложения");

            long chatId = chatIds.FirstOrDefault();

            OffersManagerBot botService = new OffersManagerBot(Configuration);

            await botService.SendMessageToSeller(chatId, messageText);       
        }
    }
}
