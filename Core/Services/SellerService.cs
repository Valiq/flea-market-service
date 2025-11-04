using Dapper;
using flea_market_service.Models;
using Microsoft.AspNetCore.Authorization;
using MySqlConnector;
using System.Data;
using static flea_market_service.Records.Requests;
using static flea_market_service.Records.Respons;

namespace flea_market_service.Core.Services
{
    public class SellerService
    {
        protected readonly IConfiguration Configuration;
        private AuthorizationService authorizationService;

        internal SellerService(IConfiguration configuration)
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

        internal async Task<bool?> CheckSellerIsActive(string telegramId)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                var sql = @"
                    SELECT Is_Active 
                    FROM SELLER 
                    WHERE Telegram_ID = @TelegramId";

                var isActive = await connection.ExecuteScalarAsync<bool?>(sql, new { TelegramId = telegramId });

                return isActive.Value;
            }
        }

        internal async Task<Seller> CreateOrUpdateSeller(string telegramId, string chatId, string chatType, string name)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                string prop = string.Empty;

                if (chatType.Equals("MANAGER"))
                    prop = "Manager_Chat_Id";

                if (chatType.Equals("DELIVERY"))
                    prop = "Delivery_Chat_Id";

                var sql = @$"
                    INSERT INTO SELLER (Telegram_ID, Name, {prop}, Is_Active)
                    VALUES (@TelegramId, @Name, @ChatId, TRUE)
                    ON DUPLICATE KEY UPDATE Name = @Name, {prop} = @ChatId;

                    SELECT 
                        ID as id,
                        Telegram_ID as telegramId,
                        Name as name,
                        Manager_Chat_Id as managerChatId,
                        Delivery_Chat_Id as deliveryChatId,
                        Is_Active as isActive,
                        Create_At as createAt
                    FROM SELLER 
                    WHERE Telegram_ID = @TelegramId;";

                var parameters = new
                {
                    TelegramId = telegramId,
                    Name = name,
                    ChatId = chatId
                };

                var seller = await connection.QueryFirstOrDefaultAsync<Seller>(sql, parameters);
                return seller;
            }
        }

        internal async Task<bool> UpdateSellerActiveStatus(long id, bool isActive, string token)
        {
            string[] roles = { "ADMIN", "MODERATOR" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                long checkGrantsResult = await CheckGrants(connection, token, roles);

                // Проверяем существование продавца
                var existsSql = "SELECT COUNT(*) FROM SELLER WHERE ID = @SellerId";
                var exists = await connection.ExecuteScalarAsync<int>(existsSql, new { SellerId = id });

                if (exists == 0)
                    throw new Exception( "Продавец с указанным ID не найден");

                // Обновляем статус Active
                var sql = "UPDATE SELLER SET Is_Active = @IsActive WHERE ID = @SellerId";

                    int affectedRows = await connection.ExecuteAsync(sql, new
                    {
                        IsActive = isActive,
                        SellerId = id
                    });

                return (affectedRows > 0);
            }
        }

        internal async Task<Seller> GetSellerById(long id, string token)
        {
            string[] roles = { "ADMIN", "MODERATOR", "VIEWER" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                long checkGrantsResult = await CheckGrants(connection, token, roles);

                var sql = @"
                        SELECT 
                            ID as id,
                            Telegram_ID as telegramId,
                            Manager_Chat_Id as managerChatId,
                            Delivery_Chat_Id as deliveryChatId
                            Name as name,
                            Is_Active as isActive,
                            DATE_FORMAT(Create_At, '%Y-%m-%dT%H:%i:%s') as createAt
                            FROM SELLER 
                         WHERE ID = @SellerId";

                var seller = await connection.QueryFirstOrDefaultAsync<Seller>(sql, new { SellerId = id });

                if (seller == null)
                    throw new Exception("Продавец с указанным ID не найден");

                return seller;
            }
        }

        internal async Task<SellersResponse> GetSellersPaged(SellerRequest request, string token, bool isBot = false)
        {
            string[] roles = {"ADMIN", "MODERATOR", "VIEWER" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                if (!isBot)
                    await CheckGrants(connection, token, roles);

                int offset = (request.pageNumber - 1) * request.pageSize;

                // Динамическое построение WHERE условия
                var whereConditions = new List<string>();
                var parameters = new DynamicParameters();

                // Фильтрация по Telegram ID (точное совпадение или частичное)
                if (!string.IsNullOrEmpty(request.telegramId.ToString()))
                {
                    if (request.telegramId.ToString().Contains("%"))
                    {
                        whereConditions.Add("Telegram_ID LIKE @TelegramId");
                        parameters.Add("TelegramId", request.telegramId);
                    }
                    else
                    {
                        whereConditions.Add("Telegram_ID = @TelegramId");
                        parameters.Add("TelegramId", request.telegramId);
                    }
                }

                // Фильтрация по name
                if (!string.IsNullOrEmpty(request.name))
                {
                    whereConditions.Add("Name LIKE @Name");
                    parameters.Add("Name", $"%{request.name}%");
                }

                // Фильтрация по статусу активности
                if (request.isActive.HasValue)
                {
                    whereConditions.Add("Is_Active = @IsActive");
                    parameters.Add("IsActive", request.isActive.Value);
                }

                // Фильтрация по дате создания
                if (!string.IsNullOrEmpty(request.createAtStartDate))
                {
                    whereConditions.Add("DATE(Create_At) >= @CreateAtStart");
                    parameters.Add("CreateAtStart", request.createAtStartDate);
                }

                if (!string.IsNullOrEmpty(request.createAtEndDate))
                {
                    whereConditions.Add("DATE(Create_At) <= @CreateAtEnd");
                    parameters.Add("CreateAtEnd", request.createAtEndDate);
                }

                string whereClause = whereConditions.Count > 0
                    ? $"WHERE {string.Join(" AND ", whereConditions)}"
                    : "";

                // Валидация сортировки
                var validSortColumns = new[] { "id", "telegramid", "name", "isactive", "createat" };
                var sortColumn = validSortColumns.Contains(request.sortBy?.ToLower())
                    ? request.sortBy
                    : "id";

                // Маппинг названий сортировки на колонки БД
                var sortColumnMapping = new Dictionary<string, string>
                {
                    ["id"] = "ID",
                    ["telegramid"] = "Telegram_Id",
                    ["name"] = "Name",
                    ["isactive"] = "Is_Active",
                    ["createat"] = "Create_At"
                };

                var orderByClause = $"ORDER BY {sortColumnMapping[sortColumn.ToLower()]} {request.sortType ?? "DESC" }";

                // Получение общего количества
                var countSql = $"SELECT COUNT(*) FROM SELLER {whereClause}";

                int totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);
                int totalPages = (int)Math.Ceiling(totalCount / (double)request.pageSize);

                // Получение данных с пагинацией
                var sql = $@"
                    SELECT 
                        ID as id,
                        Telegram_ID as telegramId,
                        Name as name,
                        Is_Active as isActive,
                        DATE_FORMAT(Create_At, '%Y-%m-%dT%H:%i:%s') as createAt
                    FROM SELLER 
                    {whereClause}
                    {orderByClause}
                    LIMIT @PageSize OFFSET @Offset";

                parameters.Add("PageSize", request.pageSize);
                parameters.Add("Offset", offset);

                var sellers = (await connection.QueryAsync<Seller>(sql, parameters)).ToList();

                return new SellersResponse(
                    sellers,
                    totalCount,
                    request.pageNumber,
                    request.pageSize,
                    totalPages
                );
            }
        }
    }
}
