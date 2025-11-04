using Dapper;
using flea_market_service.Models;
using MySqlConnector;
using System.Data;
using System.Diagnostics.Metrics;

namespace flea_market_service.Core.Services
{
    public class DictionaryService
    {
        protected readonly IConfiguration Configuration;
        protected readonly string[] Roles = { "ADMIN", "MODERATOR", "VIEWER"};

        internal DictionaryService(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        internal async Task<List<object>> GetDictionary(string dictionary, string token)
        {
            return dictionary switch
            {
                "ActionType" => (await GetDictionary<ActionType>("ACTION_TYPE", token)).Cast<object>().ToList(),
                "Role" => (await GetDictionary<Role>("ROLE", token)).Cast<object>().ToList(),
                "ModerationStatus" => (await GetDictionary<ModerationStatus>("MODERATION_STATUS", token)).Cast<object>().ToList(),
                "OfferStatus" => (await GetDictionary<OfferStatus>("OFFER_STATUS", token)).Cast<object>().ToList(),
                _ => new List<object>()
            };
        }

        private async Task<long> CheckGrants(MySqlConnection connection, string token)
        {
            AuthorizationService authorizationService = new AuthorizationService(Configuration);

            long checkGrantsResult = await authorizationService.CheckGrants(connection, token, Roles);

            if (checkGrantsResult == -1)
                throw new Exception("У вас нет необходимой роли");

            return checkGrantsResult;
        }

        private async Task<List<T>> GetDictionary<T>(string table, string token) where T : class
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                await CheckGrants(connection, token);

                string sql = $"SELECT * FROM {table}";

                return (await connection.QueryAsync<T>(sql)).ToList();
            }
        }
    }
}
