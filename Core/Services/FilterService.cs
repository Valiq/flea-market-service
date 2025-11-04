using Dapper;
using flea_market_service.Models;
using Microsoft.AspNetCore.Authorization;
using MySqlConnector;
using System;
using System.Collections.Generic;
using Telegram.Bot.Requests;
using static flea_market_service.Records.Requests;
using static flea_market_service.Records.Respons;

namespace flea_market_service.Core.Services
{
    public class FilterService
    {
        protected readonly IConfiguration Configuration;

        internal FilterService(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        internal async Task<long> CreateFilter(FilterCreateRequest request)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                SellerService service = new SellerService(Configuration);

                var sellerId = (await service.CreateOrUpdateSeller(request.sellerId, request.chatId, request.chatType, request.sellerName)).id;

                if (sellerId == null)
                    throw new Exception($"Продавец с Telegram ID {request.sellerId} не найден");

                // Создаем фильтр
                var sql = @"
                    INSERT INTO FILTER (Name, Seller_ID, Is_Active)
                    VALUES (@Name, @SellerId, TRUE);
                    SELECT LAST_INSERT_ID();";

                var newFilterId = await connection.ExecuteScalarAsync<long>(sql, new
                {
                    Name = request.name,
                    SellerId = sellerId
                });

                TagService tagService = new TagService(Configuration);

                await tagService.TagFilterHandling(connection, newFilterId, request.tags);

                return newFilterId;
            }
        }

        internal async Task<Filter?> GetFilterById(long filterId)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                var sql = @"
                    SELECT 
                        f.ID as id,
                        f.Name as name,
                        f.Is_Active as isActive,
                        DATE_FORMAT(f.Create_At, '%Y-%m-%dT%H:%i:%s') as createAt,
                        s.ID as seller_id,
                        s.Telegram_ID as seller_telegramId,
                        s.Name as seller_name,
                        s.Is_Active as seller_isActive,
                        DATE_FORMAT(s.Create_At, '%Y-%m-%dT%H:%i:%s') as seller_createAt
                    FROM FILTER f
                    INNER JOIN SELLER s ON f.Seller_ID = s.ID
                    WHERE f.ID = @FilterId";

                var filter = (await connection.QueryAsync<Filter, Seller, Filter>(
                    sql,
                    (filter, seller) =>
                    {
                        filter.seller = seller;
                        return filter;
                    },
                    new { FilterId = filterId },
                    splitOn: "seller_id"
                )).FirstOrDefault();

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
                    INNER JOIN FILTER_TAG ft ON t.ID = ft.Tag_ID
                    WHERE ft.Filter_ID = @FilterId
                    AND t.Is_Active = TRUE";

                var tags = await connection.QueryAsync<Tag>(tagsSql, new { FilterId = filterId });

                filter.tags = tags?.Any() == true ? tags.ToList() : null;

                return filter;
            }
        }

        internal async Task<List<Filter>> GetSellerFilters(string telegramId)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                // Получаем ID продавца по telegramId
                var sellerIdSql = "SELECT ID FROM SELLER WHERE Telegram_ID = @TelegramId AND Is_Active = TRUE";
                var sellerId = await connection.ExecuteScalarAsync<long?>(sellerIdSql, new { TelegramId = telegramId });

                if (sellerId == null)
                    return new List<Filter>();

                var sql = @"
                    SELECT 
                        f.ID as id,
                        f.Name as name,
                        f.Is_Active as isActive,
                        DATE_FORMAT(f.Create_At, '%Y-%m-%dT%H:%i:%s') as createAt,
                        s.ID as seller_id,
                        s.Telegram_ID as seller_telegramId,
                        s.Name as seller_name,
                        s.Is_Active as seller_isActive,
                        DATE_FORMAT(s.Create_At, '%Y-%m-%dT%H:%i:%s') as seller_createAt
                    FROM FILTER f
                    INNER JOIN SELLER s ON f.Seller_ID = s.ID
                    WHERE f.Seller_ID = @SellerId
                    ORDER BY f.Create_At DESC";

                var filters = (await connection.QueryAsync<Filter, Seller, Filter>(
                    sql,
                    (filter, seller) =>
                    {
                        filter.seller = seller;
                        return filter;
                    },
                    new { SellerId = sellerId.Value },
                    splitOn: "seller_id"
                )).ToList();

                return filters;
            }
        }

        internal async Task<bool> UpdateFilter(long filterId, FilterUpdateRequest request)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                // Проверяем существование фильтра
                var filterExistsSql = @"
                        SELECT f.ID, f.Seller_ID, s.Telegram_ID 
                        FROM FILTER f 
                        INNER JOIN SELLER s ON f.Seller_ID = s.ID 
                        WHERE f.ID = @FilterId";

                var filterInfo = await connection.QueryFirstOrDefaultAsync<(long Id, long SellerId, string TelegramId)>(
                    filterExistsSql, new { FilterId = filterId });

                if (filterInfo == default)
                    throw new Exception("Фильтр не найден");

                // Динамическое построение SQL запроса для обновления фильтра
                var updateFields = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("FilterId", filterId);

                if (!string.IsNullOrEmpty(request.name))
                {
                    updateFields.Add("Name = @Name");
                    parameters.Add("Name", request.name);
                }

                if (request.isActive.HasValue)
                {
                    updateFields.Add("Is_Active = @IsActive");
                    parameters.Add("IsActive", request.isActive.Value);
                }

                // Обновляем основные поля фильтра, если они указаны
                if (updateFields.Count > 0)
                {
                    var updateSql = $"UPDATE FILTER SET {string.Join(", ", updateFields)} WHERE ID = @FilterId";
                    await connection.ExecuteAsync(updateSql, parameters);
                }

                // Обрабатываем теги, если они указаны
                if (request.tags != null && request.tags.Length > 0)
                {
                    TagService tagService = new TagService(Configuration); 

                    await tagService.TagFilterHandling(connection, filterId, request.tags);
                }

                return true;
            }
        }

        internal async Task<bool> UpdateFilterIsActive(long[] filterIds, bool isActive)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                // Проверяем, что массив не пустой
                if (filterIds == null || filterIds.Length == 0)
                {
                    throw new ArgumentException("Массив идентификаторов фильтров не может быть пустым");
                }

                // Удаляем дубликаты и проверяем на валидность ID
                var distinctFilterIds = filterIds.Distinct().Where(id => id > 0).ToArray();

                if (distinctFilterIds.Length == 0)
                {
                    throw new ArgumentException("Нет валидных идентификаторов фильтров");
                }

                // Проверяем существование фильтров
                var checkExistsSql = @"
                    SELECT COUNT(*) 
                    FROM FILTER 
                    WHERE ID IN @FilterIds";

                var existingCount = await connection.ExecuteScalarAsync<int>(
                    checkExistsSql, new { FilterIds = distinctFilterIds });

                if (existingCount != distinctFilterIds.Length)
                {
                    throw new Exception($"Найдено только {existingCount} из {distinctFilterIds.Length} запрошенных фильтров");
                }

                // Обновляем статус активность для всех фильтров
                var updateSql = @"
                    UPDATE FILTER 
                    SET Is_Active = @IsActive 
                    WHERE ID IN @FilterIds";

                var parameters = new
                {
                    IsActive = isActive,
                    FilterIds = distinctFilterIds
                };

                int affectedRows = await connection.ExecuteAsync(updateSql, parameters);

                return affectedRows > 0;
            }
        }

        internal async Task<bool> DeleteFilter(long filterId)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                var deleteFilterSql = "DELETE FROM FILTER WHERE ID = @FilterId";
                int affectedRows = await connection.ExecuteAsync(deleteFilterSql, new { FilterId = filterId });

                return (affectedRows == 1);
            }
        }

        internal async Task<OffersResponse> GetOffersByFiltersPaged(long[] filterIds, int pageNumber, int pageSize, string sortBy, string sortType)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                if (filterIds == null || filterIds.Length == 0)
                    return new OffersResponse(new List<Offer>(), 0, pageNumber, pageSize, 0);

                // Получаем теги для всех фильтров
                var filterTags = await GetFilterTags(connection, filterIds);
                if (!filterTags.Any())
                    return new OffersResponse(new List<Offer>(), 0, pageNumber, pageSize, 0);

                // Получаем все предложения с тегами
                var offerTagsDict = await GetAllOffersWithTags(connection);

                // Вычисляем релевантность для каждого предложения с учетом частичного совпадения тегов
                var scoredOffers = await CalculateOfferScores(connection, offerTagsDict, filterTags, filterIds);


                OfferService offerService = new OfferService(Configuration);
               
                return await offerService.GetOffersByIds(scoredOffers.ToArray(), pageNumber, pageSize, sortBy, sortType);
            }
        }

        private async Task<Dictionary<long, List<(long TagId, string TagName)>>> GetFilterTags(MySqlConnection connection, long[] filterIds)
        {
            var sql = @"
                SELECT ft.Filter_ID, t.ID as TagId, t.Name as TagName
                FROM FILTER_TAG ft
                INNER JOIN TAG t ON ft.Tag_ID = t.ID
                WHERE ft.Filter_ID IN @FilterIds AND t.Is_Active = TRUE";

            var results = await connection.QueryAsync<(long FilterId, long TagId, string TagName)>(sql, new { FilterIds = filterIds });

            return results
                .GroupBy(x => x.FilterId)
                .ToDictionary(g => g.Key, g => g.Select(x => (x.TagId, x.TagName)).ToList());
        }

        private async Task<Dictionary<long, List<(long TagId, string TagName)>>> GetAllOffersWithTags(MySqlConnection connection)
        {

            var sql = @"
                 SELECT 
                     o.ID as OfferId,
                     t.ID as TagId,
                     t.Name as TagName
                 FROM OFFER o
                 INNER JOIN OFFER_STATUS os ON o.Status_ID = os.ID AND os.Actual = TRUE
                 INNER JOIN SELLER s ON o.Seller_ID = s.ID AND s.Is_Active = TRUE
                 LEFT JOIN OFFER_TAG ot ON o.ID = ot.Offer_ID
                 LEFT JOIN TAG t ON ot.Tag_ID = t.ID AND t.Is_Active = TRUE
                 WHERE o.Status_ID IN (SELECT ID FROM OFFER_STATUS WHERE Code IN ('ACTIVE', 'PUBLISHED'))";

            var results = await connection.QueryAsync<(long OfferId, long TagId, string TagName)>(
                sql
            );

            var offerTags = new Dictionary<long, List<(long TagId, string TagName)>>();

            foreach (var result in results)
            {
                if (!offerTags.ContainsKey(result.OfferId))
                {
                    offerTags[result.OfferId] = new List<(long TagId, string TagName)>();
                }

                // Добавляем тег только если он существует (TagId > 0)
                if (result.TagId > 0 && !string.IsNullOrEmpty(result.TagName))
                {
                    // Проверяем, нет ли уже такого тега в списке
                    if (!offerTags[result.OfferId].Any(t => t.TagId == result.TagId))
                    {
                        offerTags[result.OfferId].Add((result.TagId, result.TagName));
                    }
                }
            }

            return offerTags;
        }

        private async Task<List<long>> CalculateOfferScores(MySqlConnection connection, Dictionary<long, List<(long TagId, string TagName)>> offerTags, Dictionary<long, List<(long TagId, string TagName)>> filterTags, long[] filterIds)
        {
            var scoredOffers = new List<long>();

            foreach (var offerTag in offerTags)
            {
                var offerTagList = offerTag.Value;

                double maxScore = 0;

                foreach (var filterId in filterIds)
                {
                    if (!filterTags.TryGetValue(filterId, out var filterTagList))
                        continue;

                    var totalFilterTags = filterTagList.Count;
                    if (totalFilterTags == 0)
                        continue;

                    // Считаем совпадения с учетом частичного совпадения тегов
                    var matchingTags = 0;

                    foreach (var filterTag in filterTagList)
                    {
                        // Проверяем полное совпадение по ID
                        if (offerTagList.Any(ot => ot.TagId == filterTag.TagId))
                        {
                            matchingTags++;
                            continue;
                        }

                        // Проверяем частичное совпадение по названию
                        var hasPartialMatch = offerTagList.Any(ot =>
                            IsSimilarEnough(filterTag.TagName, ot.TagName, 0.6));

                        if (hasPartialMatch)
                        {
                            matchingTags++;
                        }
                    }

                    var score = (double)matchingTags / totalFilterTags * 100;

                    if (score > maxScore)
                        maxScore = score;
                }

                // Получаем порог соответствия из настроек
                var sql = "SELECT Compliance_Percentage FROM APP_SETTINGS ORDER BY ID LIMIT 1";
                double compliancePercentage = await connection.ExecuteScalarAsync<int?>(sql) ?? 45;

                if (maxScore >= compliancePercentage)
                {
                    scoredOffers.Add((offerTag.Key));
                }
            }

            return scoredOffers;
        }

        private bool IsSimilarEnough(string tag1, string tag2, double minSimilarity = 0.6)
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
    }
}