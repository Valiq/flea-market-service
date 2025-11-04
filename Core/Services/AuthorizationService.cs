using MySqlConnector;
using Dapper;
using static flea_market_service.Records.Respons;
using static BCrypt.Net.BCrypt;
using flea_market_service.Models;
using static flea_market_service.Records.Requests;

namespace flea_market_service.Core.Services
{
    public class AuthorizationService
    {
        protected readonly IConfiguration Configuration;

        internal AuthorizationService(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        internal async Task<long> CheckGrants(MySqlConnection connection, string token, string[] roles)
        {
            var user = await GetUserByToken(connection, token);

            if (user is not null)
                if (roles.Contains(user.role.code))
                    return user.id;

            return -1;
        }

        internal async Task<string> CreateSession(LoginRequest request)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                string sql = @"SELECT * FROM User WHERE Email = @Email";

                User user = await connection.QueryFirstAsync<User>(sql, new { Email = request.email });

                if (!Verify(request.password, user.password))
                    return string.Empty;

                sql = @"INSERT INTO User_Audit (User_Id, Login_Date) VALUES (@Id, @LoginDate)";

                UserAudit audit = new UserAudit()
                {
                    id = user.id,
                    loginDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                await connection.ExecuteAsync(sql, audit);

                sql = @"UPDATE User SET Token = @Token WHERE Id = @Id";

                string token = Guid.NewGuid().ToString();

                await connection.ExecuteAsync(sql, new { Token = HashPassword(token), user.id });

                return token;
            }
        }

        internal async Task<UserResponse> GetUserByToken(string token)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                User user = await GetUserByToken(connection, token);

                if (user is null)
                    return null;

                return new UserResponse(
                    user.id,
                    user.name,
                    user.email,
                    user.phone,
                    user.role,
                    user.isDeleted,
                    user.createAt,
                    user.createBy,
                    string.Empty,
                    string.Empty
                );
            }
        }

        internal async Task<User> GetUserByToken(MySqlConnection connection, string token)
        {

            var sql = @"
                SELECT u.*, r.* 
                FROM USER u 
                INNER JOIN ROLE r ON u.Role_ID = r.ID";

            var users = await connection.QueryAsync<User, Role, User>(
                sql,
                (user, role) =>
                {
                    user.role = role;
                    return user;
                },
                splitOn: "ID"
            );

            foreach (var user in users)
            {
                if (Verify(token, user.token))
                {
                    return user;
                }
            }

            return null;
        }

        internal async Task<bool> CheckToken(MySqlConnection connection, string token, long userId)
        {
            string sql = @"SELECT * FROM User WHERE Id = @Id";

            User user = await connection.QueryFirstAsync<User>(sql, new { Id = userId });

            if (Verify(token, user.token))
                return true;
            else
                return false;
        }
    }
}
