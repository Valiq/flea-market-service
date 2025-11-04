using Dapper;
using flea_market_service.Core.Bots;
using flea_market_service.Models;
using Microsoft.AspNetCore.Authorization;
using MySqlConnector;
using System.Diagnostics;
using Telegram.Bot.Types;
using static flea_market_service.Records.Requests;
using static flea_market_service.Records.Respons;

namespace flea_market_service.Core.Services
{
    public class OfferService
    {
        protected readonly IConfiguration Configuration;
        private AuthorizationService authorizationService;

        internal OfferService(IConfiguration configuration)
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

        internal async Task<long> CreateOffer(MySqlConnection connection, MySqlTransaction transaction, long offerAuditId)
        {
            // Получаем данные из OfferAudit
            var offerAudit = await connection.QueryFirstOrDefaultAsync<OfferAuditRecord>(
                @"SELECT 
                        oa.ID, oa.Name, oa.Description, oa.Image_ID as imageId, oa.Seller_ID as sellerId, oa.Price, oa.Tags, 
                        oa.Offer_ID as offerId, oa.Action_Type_ID as actionTypeId
                    FROM OFFER_AUDIT oa 
                    WHERE oa.ID = @OfferAuditId",
                new { OfferAuditId = offerAuditId }, transaction);

            if (offerAudit == null)
            {
                throw new ArgumentException("OfferAudit не найден");
            }

            // Проверяем, не было ли уже создано предложение для этого аудита
            if (offerAudit?.offerId != null && offerAudit.offerId > 0)
            {
                throw new InvalidOperationException("Предложение уже было создано для данного аудита");
            }

            // Проверяем активность продавца
            var sellerIsActive = await connection.ExecuteScalarAsync<bool?>(
                "SELECT Is_Active FROM SELLER WHERE ID = @SellerId",
                new { SellerId = offerAudit.sellerId }, transaction);

            if (sellerIsActive == null)
            {
                throw new ArgumentException("Продавец не найден в системе");
            }

            if (!sellerIsActive.Value)
            {
                throw new InvalidOperationException("Данный продавец заблокирован в системе");
            }

            // Получаем ID статуса "ОПУБЛИКОВАНО"
            var publishedStatusId = await connection.ExecuteScalarAsync<long>("SELECT ID FROM OFFER_STATUS WHERE Code = 'PUBLISHED'", transaction: transaction);

            if (publishedStatusId == null || publishedStatusId == 0)
            {
                throw new Exception("Статус PUBLISHED не найден в системе");
            }

            try
            {
                // Создаем новое предложение
                var offerId = await connection.ExecuteScalarAsync<long>(
                    @"INSERT INTO OFFER 
                            (Name, Description, Image_ID, Seller_ID, Price, Status_ID, Create_At)
                            VALUES (@Name, @Description, @ImageId, @SellerId, @Price, @StatusId, NOW());
                            SELECT LAST_INSERT_ID();",
                    new
                    {
                        offerAudit.name,
                        offerAudit.description,
                        ImageId = offerAudit.imageId,
                        SellerId = offerAudit.sellerId,
                        offerAudit.price,
                        StatusId = publishedStatusId
                    },
                    transaction);

                // Обновляем ссылку на созданное предложение в OfferAudit
                await connection.ExecuteAsync(
                    "UPDATE OFFER_AUDIT SET Offer_ID = @OfferId WHERE ID = @OfferAuditId",
                    new { OfferId = offerId, OfferAuditId = offerAuditId },
                    transaction);

                // Обрабатываем теги
                if (!string.IsNullOrEmpty(offerAudit.tags))
                {
                    var tagsArray = offerAudit.tags.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(tag => tag.Trim())
                        .Where(tag => !string.IsNullOrEmpty(tag))
                        .ToArray();

                    if (tagsArray.Length > 0)
                    {
                        var tagService = new TagService(Configuration);
                        bool tagsHandled = await tagService.TagOfferHandling(connection, transaction, offerId, tagsArray);

                        if (!tagsHandled)
                        {
                            throw new Exception("Не удалось обработать теги для предложения");
                        }
                    }
                }

                return offerId;
            }
            catch (Exception ex)
            {
                throw new Exception($"Ошибка при создании предложения: {ex.Message}");
            }
        }

        internal async Task<bool> UpdateOffer(MySqlConnection connection, MySqlTransaction transaction, long offerAuditId)
        {
            // Получаем данные из OfferAudit
            var offerAudit = await connection.QueryFirstOrDefaultAsync<OfferAuditRecord>(
                @"SELECT 
                        oa.ID, oa.Name, oa.Description, oa.Image_ID as imageId, oa.Seller_ID as sellerId, oa.Price, oa.Tags, 
                        oa.Offer_ID as offerId, oa.Action_Type_ID as actionTypeId
                    FROM OFFER_AUDIT oa 
                    WHERE oa.ID = @OfferAuditId",
                new { OfferAuditId = offerAuditId }, transaction);

            if (offerAudit == null)
            {
                throw new ArgumentException("OfferAudit не найден");
            }

            // Проверяем, что предложение уже было создано для этого аудита
            if (offerAudit.offerId == null || offerAudit.offerId == 0)
            {
                throw new InvalidOperationException("Предложение еще не было создано для данного аудита");
            }

            // Получаем ID статуса "ОПУБЛИКОВАНО"
            var publishedStatusId = await connection.ExecuteScalarAsync<long>("SELECT ID FROM OFFER_STATUS WHERE Code = 'PUBLISHED'", transaction: transaction);

            if (publishedStatusId == null || publishedStatusId == 0)
            {
                throw new Exception("Статус PUBLISHED не найден в системе");
            }

            try
            {
                // Обновляем существующее предложение
                int rowsAffected = await connection.ExecuteAsync(
                    @"UPDATE OFFER 
                            SET Name = @Name, 
                                Description = @Description, 
                                Image_ID = @ImageId, 
                                Seller_ID = @SellerId, 
                                Price = @Price,
                                Change_At = NOW()
                            WHERE ID = @OfferId",
                    new
                    {
                        offerAudit.name,
                        offerAudit.description,
                        ImageId = offerAudit.imageId,
                        SellerId = offerAudit.sellerId,
                        offerAudit.price,
                        OfferId = offerAudit.offerId
                    },
                    transaction);

                if (rowsAffected == 0)
                {
                    throw new Exception("Не удалось обновить предложение");
                }

                // Обрабатываем теги
                if (!string.IsNullOrEmpty(offerAudit.tags))
                {
                    var tagsArray = offerAudit.tags.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(tag => tag.Trim())
                        .Where(tag => !string.IsNullOrEmpty(tag))
                        .ToArray();

                    if (tagsArray.Length > 0)
                    {
                        var tagService = new TagService(Configuration);
                        bool tagsHandled = await tagService.TagOfferHandling(connection, transaction, offerAudit.offerId.Value, tagsArray);

                        if (!tagsHandled)
                        {
                            throw new Exception("Не удалось обработать теги для предложения");
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                throw new Exception($"Ошибка при обновлении предложения: {ex.Message}");
            }
        }

        internal async Task<OffersResponse> GetOffersPaged(OfferRequest request, string token, bool isBot = false)
        {
            string[] roles = { "ADMIN", "MODERATOR", "VIEWER" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                if (!isBot)
                    await CheckGrants(connection, token, roles);

                int offset = (request.pageNumber - 1) * request.pageSize;

                // Динамическое построение WHERE условия
                var whereConditions = new List<string>();
                var parameters = new DynamicParameters();

                // Фильтрация по названию предложения
                if (!string.IsNullOrEmpty(request.name))
                {
                    whereConditions.Add("o.Name LIKE @Name");
                    parameters.Add("Name", $"%{request.name}%");
                }

                // Фильтрация по продавцу (ID)
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

                // Фильтрация по цене
                if (request.startPrice.HasValue)
                {
                    whereConditions.Add("o.Price >= @StartPrice");
                    parameters.Add("StartPrice", (decimal)request.startPrice.Value);
                }

                if (request.endPrice.HasValue)
                {
                    whereConditions.Add("o.Price <= @EndPrice");
                    parameters.Add("EndPrice", (decimal)request.endPrice.Value);
                }

                // Фильтрация по статусу
                if (request.status != null && request.status.Length > 0)
                {
                    whereConditions.Add("os.Code IN @Status");
                    parameters.Add("Status", request.status);
                }

                // Фильтрация по тегам (частичное совпадение)
                if (request.tags != null && request.tags.Length > 0)
                {
                    // Создаем условия для каждого тега
                    var tagConditions = new List<string>();
                    for (int i = 0; i < request.tags.Length; i++)
                    {
                        var tagParam = $"Tag{i}";
                        tagConditions.Add($"EXISTS (SELECT 1 FROM OFFER_TAG ot INNER JOIN TAG t ON ot.Tag_ID = t.ID WHERE ot.Offer_ID = o.ID AND t.Name LIKE @{tagParam})");
                        parameters.Add(tagParam, $"%{request.tags[i]}%");
                    }

                    whereConditions.Add($"({string.Join(" AND ", tagConditions)})");
                }

                // Фильтрация по дате последней публикации
                if (!string.IsNullOrEmpty(request.lastPushingStartDate) && DateTime.TryParse(request.lastPushingStartDate, out DateTime lastPushingStart))
                {
                    whereConditions.Add("o.Last_Pushing >= @LastPushingStart");
                    parameters.Add("LastPushingStart", lastPushingStart);
                }

                if (!string.IsNullOrEmpty(request.lastPushingEndDate) && DateTime.TryParse(request.lastPushingEndDate, out DateTime lastPushingEnd))
                {
                    whereConditions.Add("o.Last_Pushing <= @LastPushingEnd");
                    parameters.Add("LastPushingEnd", lastPushingEnd);
                }

                // Фильтрация по дате создания
                if (!string.IsNullOrEmpty(request.createAtStartDate) && DateTime.TryParse(request.createAtStartDate, out DateTime createAtStart))
                {
                    whereConditions.Add("o.Create_At >= @CreateAtStart");
                    parameters.Add("CreateAtStart", createAtStart);
                }

                if (!string.IsNullOrEmpty(request.createAtEndDate) && DateTime.TryParse(request.createAtEndDate, out DateTime createAtEnd))
                {
                    whereConditions.Add("o.Create_At <= @CreateAtEnd");
                    parameters.Add("CreateAtEnd", createAtEnd);
                }

                // Фильтрация по дате удаления
                if (!string.IsNullOrEmpty(request.changeAtStartDate) && DateTime.TryParse(request.changeAtStartDate, out DateTime changeAtStart))
                {
                    whereConditions.Add("o.Change_At >= @ChangeAtStart");
                    parameters.Add("ChangeAtStart", changeAtStart);
                }

                if (!string.IsNullOrEmpty(request.changeAtEndDate) && DateTime.TryParse(request.changeAtEndDate, out DateTime changeAtEnd))
                {
                    whereConditions.Add("o.Change_At <= @ChangeAtEnd");
                    parameters.Add("ChangeAtEnd", changeAtEnd);
                }

                string whereClause = whereConditions.Count > 0
                    ? $"WHERE {string.Join(" AND ", whereConditions)}"
                    : "";

                // Валидация и настройка сортировки
                var validSortColumns = new Dictionary<string, string>
                {
                    ["name"] = "o.Name",
                    ["price"] = "o.Price",
                    ["createdate"] = "o.Create_At",
                    ["lastpushing"] = "o.Last_Pushing",
                    ["deletedate"] = "o.Change_At",
                    ["status"] = "os.Name",
                    ["sellername"] = "s.Name",
                    ["sellerid"] = "s.Id",
                };

                var sortColumnKey = request.sortBy?.ToLower();
                string sortColumn = "o.Create_At";

                if (sortColumnKey is not null)
                {
                    sortColumn = validSortColumns.ContainsKey(sortColumnKey)
                    ? validSortColumns[sortColumnKey]
                    : "o.Create_At";
                }

                var sortDirection = request.sortType?.ToUpper() == "ASC" ? "ASC" : "DESC";
                var orderByClause = $"ORDER BY {sortColumn} {sortDirection}";

                // SQL для получения общего количества записей
                var countSql = $@"
                    SELECT COUNT(DISTINCT o.ID)
                    FROM OFFER o
                    INNER JOIN SELLER s ON o.Seller_ID = s.ID
                    INNER JOIN OFFER_STATUS os ON o.Status_ID = os.ID
                    LEFT JOIN OFFER_IMAGE oi ON o.Image_ID = oi.ID
                    {whereClause}";

                int totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);
                int totalPages = (int)Math.Ceiling(totalCount / (double)request.pageSize);

                // SQL для получения данных с пагинацией
                var sql = $@"
                    SELECT 
                        o.ID as offer_id,
                        o.Name as offer_name,
                        o.Description as offer_description,
                        o.Price as offer_price,
                        DATE_FORMAT(o.Last_Pushing, '%Y-%m-%dT%H:%i:%s') as offer_lastPushing,
                        DATE_FORMAT(o.Create_At, '%Y-%m-%dT%H:%i:%s') as offer_createAt, 
                        DATE_FORMAT(o.Change_At, '%Y-%m-%dT%H:%i:%s') as offer_changeAt, 
    
                        s.ID as seller_id,
                        s.Telegram_ID as seller_telegramId,
                        s.Name as seller_name,
                        s.Is_Active as seller_isActive,
                        DATE_FORMAT(s.Create_At, '%Y-%m-%dT%H:%i:%s') as seller_createAt,              
    
                        oi.ID as image_id,
                        oi.Name as image_name,
                        oi.Path as image_path,
                        oi.Extension as image_extension,
                        oi.Size as image_size,
                        DATE_FORMAT(oi.Create_At, '%Y-%m-%dT%H:%i:%s') as image_createAt,
    
                        os.ID as status_id,
                        os.Name as status_name,
                        os.Code as status_code,
                        os.Actual as status_actual
    
                    FROM OFFER o
                    INNER JOIN SELLER s ON o.Seller_ID = s.ID
                    INNER JOIN OFFER_STATUS os ON o.Status_ID = os.ID
                    LEFT JOIN OFFER_IMAGE oi ON o.Image_ID = oi.ID
                    {whereClause}
                    {orderByClause}
                    LIMIT @PageSize OFFSET @Offset";

                parameters.Add("PageSize", request.pageSize);
                parameters.Add("Offset", offset);

                // Получаем данные предложений
                var offersData = await connection.QueryAsync(sql, parameters);

                // Получаем ID всех предложений для загрузки тегов и модераций
                var offerIds = offersData.Select(row => (long)row.offer_id).ToList();

                // Загружаем теги для всех предложений одним запросом
                Dictionary<long, List<Tag>> offerTags = new Dictionary<long, List<Tag>>();

                if (offerIds.Any())
                {
                    var tagsSql = @"
                        SELECT 
                            ot.Offer_ID as offer_id,
                            t.ID as tag_id,
                            t.Name as tag_name,
                            t.Is_Active as tag_isActive,
                            t.Offer_Score as tag_offerScore,
                            t.Filter_Score as tag_filterScore,
                            DATE_FORMAT(t.Create_At, '%Y-%m-%dT%H:%i:%s') as tag_createAt
                        FROM OFFER_TAG ot
                        INNER JOIN TAG t ON ot.Tag_ID = t.ID
                        WHERE ot.Offer_ID IN @OfferIds
                        AND t.Is_Active = TRUE
                        ORDER BY ot.Offer_ID, t.Name";

                    var tagsData = await connection.QueryAsync(tagsSql, new { OfferIds = offerIds });

                    foreach (var tagRow in tagsData)
                    {
                        var offerId = (long)tagRow.offer_id;
                        var tag = new Tag
                        {
                            id = tagRow.tag_id,
                            name = tagRow.tag_name,
                            isActive = tagRow.tag_isActive,
                            offerScore = tagRow.tag_offerScore,
                            filterScore = tagRow.tag_filterScore,
                            createAt = tagRow.tag_createAt.ToString()
                        };

                        if (!offerTags.ContainsKey(offerId))
                        {
                            offerTags[offerId] = new List<Tag>();
                        }
                        offerTags[offerId].Add(tag);
                    }
                }

                // Загружаем последние статусы модерации для всех предложений одним запросом
                Dictionary<long, string> lastModerationStatuses = new Dictionary<long, string>();

                if (offerIds.Any())
                {
                    var moderationSql = @"
                        SELECT 
                            oa.Offer_id as offerId,
                            ms.Code as code
                        FROM MODERATION m
                        INNER JOIN OFFER_AUDIT oa ON m.Offer_Audit_ID = oa.ID
                        INNER JOIN MODERATION_STATUS ms ON m.Moderation_Status_ID = ms.ID
                        WHERE oa.Offer_id IN @OfferIds
                        AND m.ID IN (
                            SELECT MAX(m2.ID)
                            FROM MODERATION m2
                            INNER JOIN OFFER_AUDIT oa2 ON m2.Offer_Audit_ID = oa2.ID
                            WHERE oa2.Offer_ID IN @OfferIds
                            GROUP BY oa2.Offer_ID
                        )";

                    var moderationData = await connection.QueryAsync(moderationSql, new { OfferIds = offerIds });

                    foreach (var modRow in moderationData)
                    {
                        var offerId = (long)modRow.offerId;
                        var moderationStatus = new ModerationStatus
                        {
                            code = modRow.code,
                        };

                        lastModerationStatuses[offerId] = moderationStatus.code;
                    }
                }

                // Маппим данные в объекты
                var offers = offersData.Select(row =>
                {
                    var offerId = (long)row.offer_id;
                    var tags = offerTags.ContainsKey(offerId) ? offerTags[offerId] : null;

                    // Получаем последний статус модерации для предложения
                    string lastModerationStatus = null;
                    if (lastModerationStatuses.ContainsKey(offerId))
                    {
                        lastModerationStatus = lastModerationStatuses[offerId];
                    }

                    return new Offer
                    {
                        id = offerId,
                        name = row.offer_name,
                        description = row.offer_description,
                        price = row.offer_price,
                        lastPushing = row.offer_lastPushing?.ToString(),
                        createAt = row.offer_createAt.ToString(),
                        changeAt = row.offer_changeAt?.ToString(),
                        seller = new Seller
                        {
                            id = row.seller_id,
                            telegramId = row.seller_telegramId,
                            name = row.seller_name,
                            isActive = row.seller_isActive,
                            createAt = row.seller_createAt.ToString()
                        },
                        image = row.image_id != null ? new OfferImage
                        {
                            id = row.image_id,
                            name = row.image_name,
                            path = row.image_path,
                            extension = row.image_extension,
                            size = row.image_size,
                            createAt = row.image_createAt.ToString()
                        } : null,
                        status = new OfferStatus
                        {
                            id = row.status_id,
                            name = row.status_name,
                            code = row.status_code,
                            actual = row.status_actual
                        },
                        tags = tags,
                        lastModerationStatus = lastModerationStatus
                    };
                }).ToList();

                return new OffersResponse(
                    offers,
                    totalCount,
                    request.pageNumber,
                    request.pageSize,
                    totalPages
                );
            }
        }

        internal async Task<Offer> GetOffer(long offerId, string token, bool isBot = false)
        {
            string[] roles = { "ADMIN", "MODERATOR", "VIEWER" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                if (!isBot)
                    await CheckGrants(connection, token, roles);

                var sql = @"
                    SELECT 
                        o.ID as offer_id,
                        o.Name as offer_name,
                        o.Description as offer_description,
                        o.Price as offer_price,
                        DATE_FORMAT(o.Last_Pushing, '%Y-%m-%dT%H:%i:%s') as offer_lastPushing, 
                        DATE_FORMAT(o.Create_At, '%Y-%m-%dT%H:%i:%s') as offer_createAt, 
                        DATE_FORMAT(o.Change_At, '%Y-%m-%dT%H:%i:%s') as offer_changeAt, 
        
                        s.ID as seller_id,
                        s.Telegram_ID as seller_telegramId,
                        s.Name as seller_name,
                        s.Is_Active as seller_isActive,
                        DATE_FORMAT(s.Create_At, '%Y-%m-%dT%H:%i:%s') as seller_createAt,
        
                        oi.ID as image_id,
                        oi.Name as image_name,
                        oi.Path as image_path,
                        oi.Extension as image_extension,
                        oi.Size as image_size,
                        DATE_FORMAT(oi.Create_At, '%Y-%m-%dT%H:%i:%s') as image_createAt,
        
                        os.ID as status_id,
                        os.Name as status_name,
                        os.Code as status_code,
                        os.Actual as status_actual
        
                    FROM OFFER o
                    INNER JOIN SELLER s ON o.Seller_ID = s.ID
                    INNER JOIN OFFER_STATUS os ON o.Status_ID = os.ID
                    LEFT JOIN OFFER_IMAGE oi ON o.Image_ID = oi.ID
                    WHERE o.ID = @OfferId 
                    AND os.Actual = TRUE";

                var offerResult = await connection.QueryFirstOrDefaultAsync(sql, new { OfferId = offerId });

                if (offerResult == null)
                    return null;

                var offer = new Offer
                {
                    id = offerResult.offer_id,
                    name = offerResult.offer_name,
                    description = offerResult.offer_description,
                    price = offerResult.offer_price,
                    lastPushing = offerResult.offer_lastPushing?.ToString(),
                    createAt = offerResult.offer_createAt.ToString(),
                    changeAt = offerResult.offer_changeAt?.ToString(),
                    seller = new Seller
                    {
                        id = offerResult.seller_id,
                        telegramId = offerResult.seller_telegramId,
                        name = offerResult.seller_name,
                        isActive = offerResult.seller_isActive,
                        createAt = offerResult.seller_createAt.ToString()
                    },
                    image = offerResult.image_id != null ? new OfferImage
                    {
                        id = offerResult.image_id,
                        name = offerResult.image_name,
                        path = offerResult.image_path,
                        extension = offerResult.image_extension,
                        size = offerResult.image_size,
                        createAt = offerResult.image_createAt.ToString()
                    } : null,
                    status = new OfferStatus
                    {
                        id = offerResult.status_id,
                        name = offerResult.status_name,
                        code = offerResult.status_code,
                        actual = offerResult.status_actual
                    }
                };

                // Получаем теги отдельным запросом
                var tagsSql = @"
                    SELECT 
                        t.ID as id,
                        t.Name as name,
                        t.Is_Active as isActive,
                        t.Offer_Score as offerScore,
                        t.Filter_Score as filterScore,
                        DATE_FORMAT(t.Create_At, '%Y-%m-%dT%H:%i:%s') as createAt
                    FROM TAG t
                    INNER JOIN OFFER_TAG ot ON t.ID = ot.Tag_ID
                    WHERE ot.Offer_ID = @OfferId
                    AND t.Is_Active = TRUE";

                var tags = await connection.QueryAsync<Tag>(tagsSql, new { OfferId = offerId });

                offer.tags = tags?.Any() == true ? tags.ToList() : null;

                return offer;
            }
        }

        internal async Task<OffersResponse> GetOffersByIds(long[] offerIds, int pageNumber, int pageSize, string sortBy, string sortType)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                if (offerIds == null || offerIds.Length == 0)
                    return new OffersResponse(new List<Offer>(), 0, pageNumber, pageSize, 0);

                // Валидация параметров сортировки
                var allowedSortTypes = new[] { "asc", "desc" };

                var validSortColumns = new Dictionary<string, string>
                {
                    ["price"] = "o.Price",
                    ["createdate"] = "o.Create_At"
                };

                var sortField = validSortColumns.ContainsKey(sortBy) ? validSortColumns[sortBy] : "o.Create_At";
                var sortDirection = allowedSortTypes.Contains(sortType?.ToUpper()) ? sortType.ToUpper() : "desc";

                // Расчет пагинации
                int offset = (pageNumber - 1) * pageSize;
                int totalCount = offerIds.Length;
                int totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

                var paginatedOfferIds = offerIds
                    .Skip(offset)
                    .Take(pageSize)
                    .ToArray();

                if (!paginatedOfferIds.Any())
                    return new OffersResponse(new List<Offer>(), totalCount, pageNumber, pageSize, totalPages);

                // Основной запрос для предложений
                var offersSql = @"
                    SELECT 
                        o.ID as offer_id,
                        o.Name as offer_name,
                        o.Description as offer_description,
                        o.Price as offer_price,
                        DATE_FORMAT(o.Last_Pushing, '%Y-%m-%dT%H:%i:%s') as offer_lastPushing, 
                        DATE_FORMAT(o.Create_At, '%Y-%m-%dT%H:%i:%s') as offer_createAt, 
                        DATE_FORMAT(o.Change_At, '%Y-%m-%dT%H:%i:%s') as offer_changeAt, 

                        s.ID as seller_id,
                        s.Telegram_ID as seller_telegramId,
                        s.Name as seller_name,
                        s.Is_Active as seller_isActive,
                        DATE_FORMAT(s.Create_At, '%Y-%m-%dT%H:%i:%s') as seller_createAt,

                        oi.ID as image_id,
                        oi.Name as image_name,
                        oi.Path as image_path,
                        oi.Extension as image_extension,
                        oi.Size as image_size,
                        DATE_FORMAT(oi.Create_At, '%Y-%m-%dT%H:%i:%s') as image_createAt,

                        os.ID as status_id,
                        os.Name as status_name,
                        os.Code as status_code,
                        os.Actual as status_actual

                    FROM OFFER o
                    INNER JOIN SELLER s ON o.Seller_ID = s.ID
                    INNER JOIN OFFER_STATUS os ON o.Status_ID = os.ID
                    LEFT JOIN OFFER_IMAGE oi ON o.Image_ID = oi.ID
                    WHERE o.ID IN @OfferIds 
                    AND os.Actual = TRUE
                    ORDER BY 
                        CASE WHEN @SortField = 'Price' AND @SortDirection = 'ASC' THEN o.Price END ASC,
                        CASE WHEN @SortField = 'Price' AND @SortDirection = 'DESC' THEN o.Price END DESC,
                        CASE WHEN @SortField = 'Create_At' AND @SortDirection = 'ASC' THEN o.Create_At END ASC,
                        CASE WHEN @SortField = 'Create_At' AND @SortDirection = 'DESC' THEN o.Create_At END DESC";

                var parameters = new
                {
                    OfferIds = paginatedOfferIds,
                    SortField = sortField,
                    SortDirection = sortDirection
                };

                var offerResults = await connection.QueryAsync(offersSql, parameters);
                var offersDict = offerResults.ToDictionary(r => (long)r.offer_id, r => new Offer
                {
                    id = r.offer_id,
                    name = r.offer_name,
                    description = r.offer_description,
                    price = r.offer_price,
                    lastPushing = r.offer_lastPushing?.ToString(),
                    createAt = r.offer_createAt.ToString(),
                    changeAt = r.offer_changeAt?.ToString(),
                    seller = new Seller
                    {
                        id = r.seller_id,
                        telegramId = r.seller_telegramId,
                        name = r.seller_name,
                        isActive = r.seller_isActive,
                        createAt = r.seller_createAt.ToString()
                    },
                    image = r.image_id != null ? new OfferImage
                    {
                        id = r.image_id,
                        name = r.image_name,
                        path = r.image_path,
                        extension = r.image_extension,
                        size = r.image_size,
                        createAt = r.image_createAt.ToString()
                    } : null,
                    status = new OfferStatus
                    {
                        id = r.status_id,
                        name = r.status_name,
                        code = r.status_code,
                        actual = r.status_actual
                    },
                    tags = new List<Tag>()
                });

                // Загружаем все теги одним запросом
                if (offersDict.Any())
                {
                    var tagsSql = @"
                        SELECT 
                            t.ID as id,
                            t.Name as name,
                            t.Is_Active as isActive,
                            t.Offer_Score as offerScore,
                            t.Filter_Score as filterScore,
                            DATE_FORMAT(t.Create_At, '%Y-%m-%dT%H:%i:%s') as createAt,
                            ot.Offer_ID as offerId
                        FROM TAG t
                        INNER JOIN OFFER_TAG ot ON t.ID = ot.Tag_ID
                        WHERE ot.Offer_ID IN @OfferIds
                        AND t.Is_Active = TRUE";

                    var tagsResults = await connection.QueryAsync(tagsSql, new { OfferIds = paginatedOfferIds });

                    foreach (var tagResult in tagsResults)
                    {
                        if (offersDict.TryGetValue(tagResult.offerId, out Offer offer))
                        {
                            offer.tags.Add(new Tag
                            {
                                id = tagResult.id,
                                name = tagResult.name,
                                isActive = tagResult.isActive,
                                offerScore = tagResult.offerScore,
                                filterScore = tagResult.filterScore,
                                createAt = tagResult.createAt.ToString()
                            });
                        }
                    }
                }

                var offers = offersDict.Values.ToList();

                return new OffersResponse(offers, totalCount, pageNumber, pageSize, totalPages);
            }
        }

        internal async Task<List<long>> UpdateOffersStatus(OfferStatusRequest request, string token, bool isBot = false)
        {
            string[] roles = { "ADMIN" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                if (!isBot)
                    await CheckGrants(connection, token, roles);

                // Проверяем валидность входных данных
                if (request.ids == null || !request.ids.Any())
                    throw new ArgumentException("Список ID предложений не может быть пустым");

                if (string.IsNullOrEmpty(request.status))
                    throw new ArgumentException("Статус не может быть пустым");

                // Получаем ID статуса по коду
                var statusId = await connection.ExecuteScalarAsync<long?>(
                    "SELECT ID FROM OFFER_STATUS WHERE Code = @Code AND Actual = TRUE",
                    new { Code = request.status });

                if (statusId == null || statusId == 0)
                    throw new ArgumentException("Указан неверный статус");

                    long deletedStatusId = await connection.ExecuteScalarAsync<long>("SELECT ID FROM OFFER_STATUS WHERE Code = 'DELETED'");

                string existingOffersSql;

                if (isBot)
                    existingOffersSql = "SELECT ID FROM OFFER WHERE ID IN @OfferIds AND Status_ID <> @DeletedStatusId";
                else
                    existingOffersSql = "SELECT ID FROM OFFER WHERE ID IN @OfferIds";

                    // Проверяем существование предложений
                    var existingOffers = await connection.QueryAsync<long>(
                        existingOffersSql,
                        new { OfferIds = request.ids, DeletedStatusId = deletedStatusId });

                if (!existingOffers.Any())
                    throw new ArgumentException("Не найдено активных предложений с указанными ID");

                using (var transaction = await connection.BeginTransactionAsync())
                {
                    try
                    {
                        // Сначала получаем ID предложений, которые будут обновлены
                        var affectedOfferIds = await connection.QueryAsync<long>(
                            @"SELECT ID FROM OFFER 
                            WHERE ID IN @OfferIds 
                            AND Status_ID != @StatusId",
                            new
                            {
                                StatusId = statusId,
                                OfferIds = request.ids
                            },
                            transaction);

                        var affectedIds = affectedOfferIds.ToList();

                        if (!affectedIds.Any())
                            return new List<long>();

                        // Обновляем статус предложений на "DELETED" и устанавливаем дату удаления
                        int rowsAffected = await connection.ExecuteAsync(
                            @"UPDATE OFFER 
                            SET Status_ID = @StatusId,                              
                                Change_At = NOW()
                            WHERE ID IN @OfferIds 
                            AND Status_ID != @StatusId",
                            new
                            {
                                StatusId = statusId,
                                OfferIds = affectedIds
                            },
                            transaction);

                        if (!isBot)
                            foreach (var id in affectedIds)
                            {
                                string name = await connection.QueryFirstAsync<string>(@"SELECT Name FROM Offer WHERE ID = @Id", new { Id = id }, transaction);

                                if (request.status is "DELETED")
                                    await NotifySellers(id, connection, transaction, $"Вше предложение <{name}> было удалено администрацией");

                                if (request.status is "PUBLISHED")
                                    await NotifySellers(id, connection, transaction, $"Вше предложение <{name}> было опубликовано/возаблено");

                                if (request.status is "SUSPENDED")
                                    await NotifySellers(id, connection, transaction, $"Вше предложение <{name}> было приостановлено администрацией");
                            }

                        await transaction.CommitAsync();

                        return affectedIds;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        throw new Exception($"Ошибка при обновлении статусов предложений: {ex.Message}");
                    }
                }
            }
        }

        private async Task<bool> NotifySellers(long offerId, MySqlConnection connection, MySqlTransaction transaction, string messageText)
        {
            // Получаем sellerId по offerId
            var chatIds = await connection.QueryAsync<long>(
                @"SELECT s.Manager_Chat_Id
                    FROM SELLER s 
                    INNER JOIN OFFER o ON s.ID = o.Seller_ID 
                    WHERE o.ID = @OfferId 
                    AND s.Is_Active = TRUE",
                new { OfferId = offerId }, transaction);

            if (!chatIds.Any())
                throw new ArgumentException("Не найдено активных продавцов для данного предложения");

            long chatId = chatIds.FirstOrDefault();

            OffersManagerBot botService = new OffersManagerBot(Configuration);

            try
            {
                await botService.SendMessageToSeller(chatId, messageText);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nОшибка при отправке сообщения продавцу {chatId}: {ex.Message}");
                return false;
            }
        }

        internal async Task<List<string>> PushOffersWithNotifications(long[] offerIds)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                if (offerIds == null || !offerIds.Any())
                    throw new ArgumentException("Список ID предложений не может быть пустым");

                // Получаем настройки из APP_SETTINGS
                var settings = await connection.QueryFirstOrDefaultAsync<AppSettings>(
                    @"SELECT Compliance_Percentage as compliancePercentage, 
                             Seller_Pushing_Interval as sellerPushingInterval 
                      FROM APP_SETTINGS ORDER BY ID LIMIT 1");

                var compliancePercentage = settings?.compliancePercentage ?? 45;
                var pushingInterval = settings?.sellerPushingInterval ?? 6 * 60 * 60;

                using (var transaction = await connection.BeginTransactionAsync())
                {
                    try
                    {
                        List<string> messages = new List<string>();
                        int i = 1;

                        foreach (var offerId in offerIds)
                        {
                            // Проверяем интервал пуша для предложения
                            var offerPushCheck = await connection.QueryFirstOrDefaultAsync(
                                @"SELECT
                                    Name as name,
                                    Last_Pushing as lastPushing,
                                    TIMESTAMPDIFF(SECOND, Last_Pushing, NOW()) as SecondsSinceLastPush
                                FROM OFFER 
                                WHERE ID = @OfferId",
                                new { OfferId = offerId }, transaction);

                            if (offerPushCheck != null && offerPushCheck.lastPushing != null)
                            {
                                var secondsSinceLastPush = (int)offerPushCheck.SecondsSinceLastPush;

                                if (secondsSinceLastPush < pushingInterval)
                                {
                                    long remainingTime = pushingInterval - (int)TimeSpan.FromSeconds(secondsSinceLastPush).TotalSeconds;

                                    int remainingHours = (int)Math.Round(TimeSpan.FromSeconds(remainingTime).TotalHours);
                                    int remainingMinutes = (int)Math.Round(TimeSpan.FromSeconds(remainingTime).TotalMinutes);
                                    int remainingSeconds = (int)Math.Round(TimeSpan.FromSeconds(remainingTime).TotalSeconds);

                                    // Форматируем оставшееся время
                                    string timeRemaining;
                                    if (remainingHours > 0 && remainingMinutes > 0)
                                        timeRemaining = $"{remainingHours} час(ов) и {remainingMinutes} минут(ы)";
                                    else if (remainingMinutes > 0)
                                        timeRemaining = $"{remainingMinutes} минут(ы)";
                                    else if (remainingHours > 0)
                                        timeRemaining = $"{remainingHours} час(ов)";
                                    else
                                        timeRemaining = $"{remainingSeconds} секунд";

                                    messages.Add($"{i++}. {offerPushCheck.name} - ⏳ Осталось: {timeRemaining}");
                                    continue;
                                }
                            }

                            // Находим релевантных клиентов(продавцов)
                            var relevantSellers = await GetRelevantSellersForOffer(connection, transaction, offerId, compliancePercentage);

                            var botService = new OffersDeliveryBot(Configuration);

                            foreach (var seller in relevantSellers)
                            {
                                long chatId = long.Parse(seller.deliveryChatId);

                               await botService.SendOfferToSeller(chatId, offerId);
                            }

                            // Обновляем Last_Pushing для предложения
                            await connection.ExecuteAsync(
                                "UPDATE OFFER SET Last_Pushing = NOW(), Change_At = NOW() WHERE ID = @OfferId",
                                new { OfferId = offerId }, transaction);

                            messages.Add($"{i++}. {offerPushCheck.name} - 📢 Запушено!");
                        }

                        await transaction.CommitAsync();
                        return messages;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        Debug.WriteLine($"\nОшибка при выполнении пуша предложений: {ex.Message}");
                        throw;
                    }
                }
            }
        }

        private async Task<List<Seller>> GetRelevantSellersForOffer(MySqlConnection connection, MySqlTransaction transaction, long offerId, int compliancePercentage)
        {
            // Получаем все теги предложения
            var offerTagsSql = @"
                SELECT t.ID, t.Name 
                FROM OFFER_TAG ot 
                INNER JOIN TAG t ON ot.Tag_ID = t.ID 
                WHERE ot.Offer_ID = @OfferId";

            var offerTags = await connection.QueryAsync<(long Id, string Name)>(
                offerTagsSql, new { OfferId = offerId }, transaction);

            // Получаем все активные фильтры с их тегами
            var filtersSql = @"
                SELECT 
                    f.ID as FilterId,
                    f.Seller_ID as SellerId,
                    t.ID as TagId,
                    t.Name as TagName
                FROM FILTER f
                INNER JOIN FILTER_TAG ft ON f.ID = ft.Filter_ID
                INNER JOIN TAG t ON ft.Tag_ID = t.ID
                WHERE f.Is_Active = TRUE";

            var filterData = await connection.QueryAsync<(long FilterId, long SellerId, long TagId, string TagName)>(
                filtersSql, null, transaction);

            // Получаем всех активных продавцов
            var sellersSql = @"
                SELECT 
                    ID as id,
                    Telegram_ID as telegramId,
                    Manager_Chat_Id as managerChatId,
                    Delivery_Chat_Id as deliveryChatId,
                    Name as name,
                    Is_Active as isActive,
                    Create_At as createAt
                FROM SELLER 
                WHERE Is_Active = TRUE";

            var allSellers = await connection.QueryAsync<Seller>(sellersSql, null, transaction);

            // Группируем фильтры по продавцам
            var sellersWithFilters = filterData
                .GroupBy(x => x.SellerId)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(x => x.FilterId)
                          .ToDictionary(
                              gg => gg.Key,
                              gg => gg.Select(x => (x.TagId, x.TagName)).ToList()
                          )
                );

            var relevantSellers = new List<Seller>();

            foreach (var seller in allSellers)
            {
                if (!sellersWithFilters.TryGetValue(seller.id, out var sellerFilters))
                    continue;

                // Проверяем каждый фильтр продавца
                foreach (var filter in sellerFilters)
                {
                    var filterTags = filter.Value;
                    var totalTags = filterTags.Count;

                    if (totalTags == 0)
                        continue;

                    // Считаем совпадения тегов
                    var matchingTags = 0;

                    foreach (var filterTag in filterTags)
                    {
                        // Проверяем полное совпадение по ID
                        if (offerTags.Any(ot => ot.Id == filterTag.TagId))
                        {
                            matchingTags++;
                            continue;
                        }

                        // Проверяем частичное совпадение по названию с использованием IsSimilarEnough
                        var hasPartialMatch = offerTags.Any(ot =>
                            IsSimilarEnough(filterTag.TagName, ot.Name, 0.85));

                        if (hasPartialMatch)
                        {
                            matchingTags++;
                        }
                    }

                    // Рассчитываем процент совпадения
                    var matchPercentage = (double)matchingTags / totalTags;

                    if (matchPercentage * 100 >= compliancePercentage)
                    {
                        relevantSellers.Add(seller);
                        break; // Достаточно одного подходящего фильтра
                    }
                }
            }

            return relevantSellers;
        }

        private bool IsSimilarEnough(string tag1, string tag2, double minSimilarity)
        {
            if (string.IsNullOrEmpty(tag1) || string.IsNullOrEmpty(tag2))
                return false;

            tag1 = tag1.ToLower();
            tag2 = tag2.ToLower();

            // Полное совпадение
            if (tag1 == tag2)
                return true;

            // Одна строка содержит другую
            if (tag1.Contains(tag2))
            {
                double similarity = (double)tag2.Length / tag1.Length;
                return similarity >= minSimilarity;
            }

            if (tag2.Contains(tag1))
            {
                double similarity = (double)tag1.Length / tag2.Length;
                return similarity >= minSimilarity;
            }

            // Сравниваем начала строк (для случаев типа "телефон" - "телефония")
            int minLength = Math.Min(tag1.Length, tag2.Length);
            int matchingPrefix = 0;

            for (int i = 0; i < minLength; i++)
            {
                if (tag1[i] == tag2[i])
                    matchingPrefix++;
                else
                    break;
            }

            double prefixSimilarity = (double)matchingPrefix / Math.Max(tag1.Length, tag2.Length);
            return prefixSimilarity >= minSimilarity;
        }

        internal async Task<bool> DeleteExpiredOffers()
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                // Получаем время жизни предложения из настроек
                var appSettings = await connection.QueryFirstOrDefaultAsync<AppSettings>(
                    "SELECT Offer_Life_Time as offerLifeTime FROM APP_SETTINGS ORDER BY ID LIMIT 1");

                if (appSettings == null)
                    throw new Exception("Настройки приложения не найдены");

                var offerLifeTimeMs = appSettings.offerLifeTime;

                // Конвертируем миллисекунды в дни для SQL запроса
                var offerLifeTimeDays = (int)Math.Ceiling(offerLifeTimeMs / (1000.0 * 60 * 60 * 24));

                using (var transaction = await connection.BeginTransactionAsync())
                {
                    try
                    {
                        // Получаем ID статуса "DELETED"
                        var deletedStatusId = await connection.ExecuteScalarAsync<long?>(
                            "SELECT ID FROM OFFER_STATUS WHERE Code = 'DELETED' AND Actual = TRUE",
                            transaction: transaction);

                        if (deletedStatusId == null || deletedStatusId == 0)
                        {
                            throw new Exception("Статус 'DELETED' не найден в системе");
                        }

                        // Находим просроченные предложения
                        var expiredOffers = await connection.QueryAsync(
                            @"SELECT 
                                o.ID as OfferId,
                                o.Name as OfferName,
                                o.Create_At as CreateAt,
                                TIMESTAMPDIFF(SECOND, o.Create_At, NOW()) as SecondsSinceCreation,
                                s.ID as SellerId,
                                s.Name as SellerName
                            FROM OFFER o
                            INNER JOIN SELLER s ON o.Seller_ID = s.ID
                            INNER JOIN OFFER_STATUS os ON o.Status_ID = os.ID
                            WHERE o.Create_At < DATE_SUB(NOW(), INTERVAL @LifeTimeDays DAY)
                            AND os.Code != 'DELETED'",
                            new { LifeTimeDays = offerLifeTimeDays },
                            transaction);

                        var expiredOfferList = expiredOffers.ToList();

                        List<long> expiredOfferIds = expiredOfferList.Select(x => (long)x.OfferId).ToList();

                        if (expiredOfferIds.Count() > 0)
                            return true;

                        // Обновляем статус предложений на "DELETED" и устанавливаем дату удаления
                        int rowsAffected = await connection.ExecuteAsync(
                                @"UPDATE OFFER 
                            SET Status_ID = @StatusId,
                                Change_At = NOW()
                            WHERE ID IN @ExpiredOfferIds",
                                new
                                {
                                    StatusId = deletedStatusId,
                                    ExpiredOfferIds = expiredOfferIds
                                },
                                transaction);

                        //Отправка уведомления продавцу
                        foreach (var id in expiredOfferIds)
                        {
                            string name = await connection.QueryFirstAsync<string>(@"SELECT Name FROM Offer WHERE ID = @Id", new { Id = id });

                            await NotifySellers(id, connection, transaction, $"Вше предложение <{name}> было удалено по истечению времени публикации");
                        }

                        await transaction.CommitAsync();

                        Debug.WriteLine($"\nrowsAffected {rowsAffected}; TotalExpired {expiredOfferList.Count}; OfferLifeTimeMs {offerLifeTimeMs}; OfferLifeTimeDays {offerLifeTimeDays}");

                        return true;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        throw new Exception($"Ошибка при удалении просроченных предложений: {ex.Message}");
                    }
                }
            }
        }
    }
}
