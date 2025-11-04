using Dapper;
using flea_market_service.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using static flea_market_service.Records.Requests;

namespace flea_market_service.Core.Services
{
    public class AppSettingsService
    {
        protected readonly IConfiguration Configuration;
        private AuthorizationService authorizationService;
        protected readonly string[] Roles = { "ADMIN" };

        internal AppSettingsService(IConfiguration configuration)
        {
            Configuration = configuration;
            authorizationService = new AuthorizationService(Configuration);
        }

        private async Task<long> CheckGrants(MySqlConnection connection, string token)
        {
            long checkGrantsResult = await authorizationService.CheckGrants(connection, token, Roles);

            if (checkGrantsResult == -1)
                throw new Exception("У вас нет необходимой роли");

            return checkGrantsResult;
        }

        internal async Task<AppSettings> GetAppSettings(string token)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                await CheckGrants(connection, token);

                var sql = @"
                    SELECT 
                        ID as id,
                        Seller_Pushing_Interval as sellerPushingInterval,
                        Max_Seller_Offers as maxSellerOffers,
                        Max_Seller_Filters as maxSellerFilters,
                        Offer_Life_Time as offerLifeTime,
                        Min_Offer_Score as minOfferScore,
                        Min_Filter_Score as minFilterScore,
                        Compliance_Percentage as compliancePercentage
                    FROM APP_SETTINGS 
                    ORDER BY ID 
                    LIMIT 1";

                var appSettings = await connection.QueryFirstOrDefaultAsync<AppSettings>(sql);

                return appSettings;
            }
        }

        internal async Task<bool> UpdateAppSettings(long id, AppSettingsRequest request, string token)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                await CheckGrants(connection, token);

                // Проверка существования записи
                var existsSql = "SELECT COUNT(*) FROM APP_SETTINGS WHERE ID = @Id";
                var exists = await connection.ExecuteScalarAsync<int>(existsSql, new { Id = id });

                if (exists == 0)
                    throw new Exception("Настройки с указанным ID не найдены");

                // Динамическое построение SQL запроса
                var updateFields = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("Id", id);

                if (request.sellerPushingInterval.HasValue)
                {
                    if (request.sellerPushingInterval.Value <= 0)
                        throw new Exception("Seller pushing interval должен быть положительным числом");

                    updateFields.Add("Seller_Pushing_Interval = @SellerPushingInterval");
                    parameters.Add("SellerPushingInterval", request.sellerPushingInterval.Value);
                }

                if (request.maxSellerOffers.HasValue)
                {
                    if (request.maxSellerOffers.Value <= 0)
                        throw new Exception("Max seller offers должен быть положительным числом");

                    updateFields.Add("Max_Seller_Offers = @MaxSellerOffers");
                    parameters.Add("MaxSellerOffers", request.maxSellerOffers.Value);
                }

                if (request.maxSellerFilters.HasValue)
                {
                    if (request.maxSellerFilters.Value <= 0)
                        throw new Exception("Max seller filters должен быть положительным числом");

                    updateFields.Add("Max_Seller_Filters = @MaxSellerFilters");
                    parameters.Add("MaxSellerFilters", request.maxSellerFilters.Value);
                }

                if (request.offerLifeTime.HasValue)
                {
                    if (request.offerLifeTime.Value <= 0)
                        throw new Exception("Offer life time должен быть положительным числом");

                    updateFields.Add("Offer_Life_Time = @OfferLifeTime");
                    parameters.Add("OfferLifeTime", request.offerLifeTime.Value);
                }

                if (request.minOfferScore.HasValue)
                {
                    if (request.minOfferScore.Value < 0)
                        throw new Exception("Max seller filters должен быть положительным числом");

                    updateFields.Add("Min_Offer_Score = @MinOfferScore");
                    parameters.Add("MinOfferScore", request.minOfferScore.Value);
                }

                if (request.minFilterScore.HasValue)
                {
                    if (request.minFilterScore.Value < 0)
                        throw new Exception("Min filter score не может быть отрицательным");

                    updateFields.Add("Min_Filter_Score = @MinFilterScore");
                    parameters.Add("MinFilterScore", request.minFilterScore.Value);
                }

                if (request.compliancePercentage.HasValue)
                {
                    if (request.compliancePercentage.Value < 0 || request.compliancePercentage.Value > 100)
                        throw new Exception("Compliance percentage должен быть между 0 и 100.");

                    updateFields.Add("Compliance_Percentage = @CompliancePercentage");
                    parameters.Add("CompliancePercentage", request.compliancePercentage.Value);
                }

                // Если не указано ни одного поля для обновления
                if (updateFields.Count == 0)
                    return true;

                var sql = $@"UPDATE APP_SETTINGS SET {string.Join(", ", updateFields)} WHERE ID = @Id";

                int affectedRows = await connection.ExecuteAsync(sql, parameters);

                return (affectedRows > 0);
            }
        }
    }
}
