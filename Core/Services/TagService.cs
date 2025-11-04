using Dapper;
using flea_market_service.Models;
using MySqlConnector;
using System.Data;
using System.Diagnostics;

namespace flea_market_service.Core.Services
{
    public class TagService
    {
        protected readonly IConfiguration Configuration;

        internal TagService(IConfiguration configuration) 
        { 
            Configuration = configuration;
        }

        internal async Task<bool> TagFilterHandling(MySqlConnection connection, long filterId, string[] tags)
        {
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                string getCurrentTagsSql;

                if (tags == null || tags.Length == 0)
                {
                    // Если тегов нет, удаляем все существующие связи и уменьшаем счетчики
                    getCurrentTagsSql = "SELECT Tag_ID FROM FILTER_TAG WHERE Filter_ID = @FilterId";
                    var currentTagIds = await connection.QueryAsync<long>(getCurrentTagsSql, new { FilterId = filterId }, transaction);

                    // Удаляем связи
                    var deleteSql = "DELETE FROM FILTER_TAG WHERE Filter_ID = @FilterId";
                    await connection.ExecuteAsync(deleteSql, new { FilterId = filterId }, transaction);

                    // Уменьшаем счетчики для всех удаленных тегов
                    foreach (var tagId in currentTagIds)
                    {
                        var updateScoreSql = @"
                            UPDATE TAG 
                            SET Filter_score = GREATEST(0, Filter_score - 1) 
                            WHERE ID = @TagId";
                        await connection.ExecuteAsync(updateScoreSql, new { TagId = tagId }, transaction);
                    }

                    await transaction.CommitAsync();
                    return true;
                }

                var lowerCaseTags = tags.Where(t => !string.IsNullOrWhiteSpace(t))
                                       .Select(t => t.Trim().ToLower())
                                       .Distinct()
                                       .ToArray();

                // Получаем текущие теги фильтра
                getCurrentTagsSql = @"
                    SELECT t.ID, t.Name 
                    FROM TAG t 
                    INNER JOIN FILTER_TAG ft ON t.ID = ft.Tag_ID 
                    WHERE ft.Filter_ID = @FilterId";

                var currentTags = await connection.QueryAsync<(long Id, string Name)>(
                    getCurrentTagsSql, new { FilterId = filterId }, transaction);

                // Определяем теги для удаления и добавления
                var currentTagNames = currentTags.Select(ct => ct.Name.ToLower()).ToHashSet();
                var newTagNames = lowerCaseTags.ToHashSet();

                var tagsToRemove = currentTags.Where(ct => !newTagNames.Contains(ct.Name.ToLower())).ToList();
                var tagsToAdd = newTagNames.Where(nt => !currentTagNames.Contains(nt)).ToList();

                // Удаляем старые связи и уменьшаем счетчики
                foreach (var tagToRemove in tagsToRemove)
                {
                    var deleteRelationSql = "DELETE FROM FILTER_TAG WHERE Filter_ID = @FilterId AND Tag_ID = @TagId";
                    await connection.ExecuteAsync(deleteRelationSql,
                        new { FilterId = filterId, TagId = tagToRemove.Id }, transaction);

                    var decreaseScoreSql = @"
                        UPDATE TAG 
                        SET Filter_score = GREATEST(0, Filter_score - 1) 
                        WHERE ID = @TagId";
                    await connection.ExecuteAsync(decreaseScoreSql,
                        new { TagId = tagToRemove.Id }, transaction);
                }

                // Добавляем новые связи и увеличиваем счетчики
                foreach (var tagName in tagsToAdd)
                {
                    if (string.IsNullOrWhiteSpace(tagName))
                        continue;

                    var trimmedTagName = tagName.Trim();

                    // Ищем тег по имени
                    var tagSql = "SELECT ID, Filter_score FROM TAG WHERE LOWER(Name) = LOWER(@Name)";
                    var tag = await connection.QueryFirstOrDefaultAsync<(long Id, int Score)?>(
                        tagSql, new { Name = trimmedTagName }, transaction);

                    long tagId;

                    // Если тег не существует, создаем его
                    if (tag == null)
                    {
                        var insertTagSql = @"
                            INSERT INTO TAG (Name, Is_Active, Filter_score)
                            VALUES (@Name, TRUE, 1);
                            SELECT LAST_INSERT_ID();";

                        tagId = await connection.ExecuteScalarAsync<long>(insertTagSql,
                            new { Name = trimmedTagName }, transaction);
                    }
                    else
                    {
                        tagId = tag.Value.Id;

                        // Увеличиваем счетчик для существующего тега
                        var increaseScoreSql = "UPDATE TAG SET Filter_score = Filter_score + 1 WHERE ID = @TagId";
                        await connection.ExecuteAsync(increaseScoreSql,
                            new { TagId = tagId }, transaction);
                    }

                    // Создаем связь
                    var insertRelationSql = @"
                        INSERT INTO FILTER_TAG (Filter_ID, Tag_ID)
                        VALUES (@FilterId, @TagId)";

                    await connection.ExecuteAsync(insertRelationSql,
                        new { FilterId = filterId, TagId = tagId }, transaction);
                }

                await transaction.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Debug.WriteLine($"\nОшибка при обработке тегов фильтра: {ex.Message}");
                return false;
            }
        }

        internal async Task<bool> TagOfferHandling(MySqlConnection connection, MySqlTransaction transaction, long offerId, string[] tags)
        {
            try
            {
                string getCurrentTagsSql;

                if (tags == null || tags.Length == 0)
                {
                    // Если тегов нет, удаляем все существующие связи и уменьшаем счетчики
                    getCurrentTagsSql = "SELECT Tag_ID FROM OFFER_TAG WHERE Offer_ID = @OfferId";
                    var currentTagIds = await connection.QueryAsync<long>(getCurrentTagsSql, new { OfferId = offerId }, transaction);

                    // Удаляем связи
                    var deleteSql = "DELETE FROM OFFER_TAG WHERE Offer_ID = @OfferId";
                    await connection.ExecuteAsync(deleteSql, new { OfferId = offerId }, transaction);

                    // Уменьшаем счетчики для всех удаленных тегов
                    foreach (var tagId in currentTagIds)
                    {
                        var updateScoreSql = @"
                            UPDATE TAG 
                            SET Offer_score = GREATEST(0, Offer_score - 1) 
                            WHERE ID = @TagId";
                        await connection.ExecuteAsync(updateScoreSql, new { TagId = tagId }, transaction);
                    }

                    return true;
                }

                var lowerCaseTags = tags.Where(t => !string.IsNullOrWhiteSpace(t))
                                       .Select(t => t.Trim().ToLower())
                                       .Distinct()
                                       .ToArray();

                // Получаем текущие теги предложения
                getCurrentTagsSql = @"
                    SELECT t.ID, t.Name 
                    FROM TAG t 
                    INNER JOIN OFFER_TAG ot ON t.ID = ot.Tag_ID 
                    WHERE ot.Offer_ID = @OfferId";

                var currentTags = await connection.QueryAsync<(long Id, string Name)>(
                    getCurrentTagsSql, new { OfferId = offerId }, transaction);

                // Определяем теги для удаления и добавления
                var currentTagNames = currentTags.Select(ct => ct.Name.ToLower()).ToHashSet();
                var newTagNames = lowerCaseTags.ToHashSet();

                var tagsToRemove = currentTags.Where(ct => !newTagNames.Contains(ct.Name.ToLower())).ToList();
                var tagsToAdd = newTagNames.Where(nt => !currentTagNames.Contains(nt)).ToList();

                // Удаляем старые связи и уменьшаем счетчики
                foreach (var tagToRemove in tagsToRemove)
                {
                    var deleteRelationSql = "DELETE FROM OFFER_TAG WHERE Offer_ID = @OfferId AND Tag_ID = @TagId";
                    await connection.ExecuteAsync(deleteRelationSql,
                        new { OfferId = offerId, TagId = tagToRemove.Id }, transaction);

                    var decreaseScoreSql = @"
                        UPDATE TAG 
                        SET Offer_score = GREATEST(0, Offer_score - 1) 
                        WHERE ID = @TagId";
                    await connection.ExecuteAsync(decreaseScoreSql,
                        new { TagId = tagToRemove.Id }, transaction);
                }

                // Добавляем новые связи и увеличиваем счетчики
                foreach (var tagName in tagsToAdd)
                {
                    if (string.IsNullOrWhiteSpace(tagName))
                        continue;

                    var trimmedTagName = tagName.Trim();

                    // Ищем тег по имени
                    var tagSql = "SELECT ID, Offer_score FROM TAG WHERE LOWER(Name) = LOWER(@Name)";
                    var tag = await connection.QueryFirstOrDefaultAsync<(long Id, int Score)?>(
                        tagSql, new { Name = trimmedTagName }, transaction);

                    long tagId;

                    // Если тег не существует, создаем его
                    if (tag == null)
                    {
                        var insertTagSql = @"
                            INSERT INTO TAG (Name, Is_Active, Offer_score)
                            VALUES (@Name, TRUE, 1);
                            SELECT LAST_INSERT_ID();";

                        tagId = await connection.ExecuteScalarAsync<long>(insertTagSql,
                            new { Name = trimmedTagName }, transaction);
                    }
                    else
                    {
                        tagId = tag.Value.Id;

                        // Увеличиваем счетчик для существующего тега
                        var increaseScoreSql = "UPDATE TAG SET Offer_score = Offer_score + 1 WHERE ID = @TagId";
                        await connection.ExecuteAsync(increaseScoreSql,
                            new { TagId = tagId }, transaction);
                    }

                    // Создаем связь
                    var insertRelationSql = @"
                        INSERT INTO OFFER_TAG (Offer_ID, Tag_ID)
                        VALUES (@OfferId, @TagId)";

                    await connection.ExecuteAsync(insertRelationSql,
                        new { OfferId = offerId, TagId = tagId }, transaction);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"\nОшибка при обработке тегов предложения: {ex.Message}");
                return false;
            }
        }

        internal async Task<List<Tag>> GetTagsByPopularity(string tagName, int limit = 25)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                // Динамическое построение условий
                var whereConditions = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("Limit", limit);

                // Базовое условие активности
                whereConditions.Add("t.Is_Active = TRUE");

                // Фильтр по частичному соответствию названия тега (без учета регистра)
                if (!string.IsNullOrEmpty(tagName))
                {
                    whereConditions.Add("LOWER(t.Name) LIKE LOWER(@NamePattern)");
                    parameters.Add("NamePattern", $"%{tagName}%");
                }

                string whereClause = whereConditions.Count > 0 ? $"WHERE {string.Join(" AND ", whereConditions)}" : "";

                var sql = $@"
                    SELECT 
                        t.ID as id,
                        t.Name as name,
                        t.Is_Active as isActive,
                        t.Offer_Score as offerScore,
                        t.Filter_Score as filterScore,
                        DATE_FORMAT(t.Create_At, '%Y-%m-%dT%H:%i:%s') as createAt
                    FROM TAG t
                    {whereClause}
                    ORDER BY t.Offer_Score DESC
                    LIMIT @Limit";

                var tags = (await connection.QueryAsync<Tag>(sql, parameters)).ToList();
                return tags;
            }
        }

        internal async Task<bool> DeleteLowScoreTags()
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();
                using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    // Получаем минимальные score из настроек приложения
                    var settingsSql = @"
                        SELECT Min_Offer_Score, Min_Filter_Score 
                        FROM APP_SETTINGS 
                        ORDER BY ID 
                        LIMIT 1";

                    var settings = await connection.QueryFirstOrDefaultAsync<(int MinOfferScore, int MinFilterScore)>(
                        settingsSql, transaction: transaction);

                    // Находим теги для удаления
                    var tagsToDeleteSql = @"
                        SELECT t.ID
                        FROM TAG t
                        WHERE t.Offer_Score <= @MinOfferScore
                        AND t.Filter_Score <= @MinFilterScore";

                    var tagsToDelete = await connection.QueryAsync<long>(
                        tagsToDeleteSql,
                        new { settings.MinOfferScore, settings.MinFilterScore },
                        transaction);

                    if (!tagsToDelete.Any())
                        return true;

                    // Удаляем связи из OFFER_TAG
                    var deleteOfferTagsSql = "DELETE FROM OFFER_TAG WHERE Tag_ID IN @TagIds";
                    int offerTagsDeleted = await connection.ExecuteAsync(
                        deleteOfferTagsSql,
                        new { TagIds = tagsToDelete },
                        transaction);

                    // Удаляем связи из FILTER_TAG
                    var deleteFilterTagsSql = "DELETE FROM FILTER_TAG WHERE Tag_ID IN @TagIds";
                    int filterTagsDeleted = await connection.ExecuteAsync(
                        deleteFilterTagsSql,
                        new { TagIds = tagsToDelete },
                        transaction);

                    // Удаляем сами теги
                    var deleteTagsSql = "DELETE FROM TAG WHERE ID IN @TagIds";
                    int tagsDeleted = await connection.ExecuteAsync(
                        deleteTagsSql,
                        new { TagIds = tagsToDelete },
                        transaction);

                    await transaction.CommitAsync();

                    Debug.WriteLine($"\nУдалено тегов: {tagsDeleted}, связей с предложениями: {offerTagsDeleted}, связей с фильтрами: {filterTagsDeleted}");

                    return true;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    Debug.WriteLine($"\nОшибка при удалении тегов с низким рейтингом: {ex.Message}");
                    return false;
                }
            }
        }
    }
}