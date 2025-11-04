using Dapper;
using flea_market_service.Models;
using MySqlConnector;
using System.Data;
using static flea_market_service.Records.Requests;
using static flea_market_service.Records.Respons;
using static BCrypt.Net.BCrypt;
using System.Linq.Expressions;

namespace flea_market_service.Core.Services
{
    public class UserService
    {
        protected readonly IConfiguration Configuration;
        private AuthorizationService authorizationService;

        internal UserService(IConfiguration configuration)
        {
            Configuration = configuration;
            authorizationService = new AuthorizationService(Configuration);
        }

        private async Task<long> CheckGrants(MySqlConnection connection, string token, string[]  roles)
        {
            long checkGrantsResult = await authorizationService.CheckGrants(connection, token, roles);

            if (checkGrantsResult == -1)
                throw new Exception("У вас нет необходимой роли");

            return checkGrantsResult;
        }

        internal async Task<UserResponse> GetUserById(long id, string token)
        {
            string[] roles = { "ADMIN", "MODERATOR", "VIEWER" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                long checkGrantsResult = await CheckGrants(connection, token, roles);

                var sql = @"
                    SELECT 
                        u.ID, 
                        u.Name, 
                        u.Email, 
                        u.Phone,
                        u.Is_Deleted as isDeleted, 
                        DATE_FORMAT(u.Create_At, '%Y-%m-%dT%H:%i:%s') as createAt,
                        r.ID, 
                        r.Name, 
                        r.Code, 
                        r.Rank, 
                        r.Actual,
                        creator.ID, 
                        creator.Name as creatorName,
                        DATE_FORMAT(ua.Last_Login_Date, '%Y-%m-%dT%H:%i:%s') as lastLoginDate
                    FROM USER u 
                    INNER JOIN ROLE r ON u.Role_ID = r.ID
                    LEFT JOIN USER creator ON u.Create_By = creator.ID
                    LEFT JOIN (
                        SELECT User_ID, MAX(Login_Date) as Last_Login_Date
                        FROM USER_AUDIT
                        WHERE User_ID = @Id
                        GROUP BY User_ID
                    ) ua ON u.ID = ua.User_ID
                    WHERE u.Id = @Id";

                var users = (await connection.QueryAsync<User, Role, User, string, UserResponse>(
                    sql,
                    (user, role, creator, lastLoginDate) =>
                    {
                        return new UserResponse
                        (
                            id: user.id,
                            name: user.name,
                            email: user.email,
                            phone: user.phone,
                            role: role,
                            isDeleted: user.isDeleted,
                            createAt: user.createAt,
                            creatorId: creator?.id ?? 0,
                            creatorName: creator?.name,
                            lastLoginDate: lastLoginDate // уже строка
                        );
                    },
                    new { Id = id },
                    splitOn: "ID,ID,lastLoginDate" // разделяем User, Role, Creator и lastLoginDate
                )).ToList();

                if (users.Count > 0)
                    return users[0];
                else
                    return null;
            }
        }

        internal async Task<UsersResponse> GetUsersPaged(UserRequest request, string token)
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

                // Базовое условие
                whereConditions.Add("u.Is_Deleted = @IsDeleted");
                if (request.isDeleted is not null)
                    parameters.Add("IsDeleted", request.isDeleted);
                else
                    parameters.Add("IsDeleted", false);

                // Фильтрация по email
                if (!string.IsNullOrEmpty(request.email))
                {
                    whereConditions.Add("u.Email LIKE @Email");
                    parameters.Add("Email", $"%{request.email}%");
                }

                // Фильтрация по name
                if (!string.IsNullOrEmpty(request.name))
                {
                    whereConditions.Add("u.Name LIKE @Name");
                    parameters.Add("Name", $"%{request.name}%");
                }

                // Фильтрация по phone
                if (!string.IsNullOrEmpty(request.phone))
                {
                    whereConditions.Add("u.Phone LIKE @Phone");
                    parameters.Add("Phone", $"%{request.phone}%");
                }

                // Фильтрация по ролям (исправлено - ищем по Code или Name)
                if (request.role != null && request.role.Length > 0)
                {
                    whereConditions.Add("(r.Code IN @Roles OR r.Name IN @Roles)");
                    parameters.Add("Roles", request.role);
                }

                // Фильтрация по создателю (по имени пользователя, а не ID)
                if (!string.IsNullOrEmpty(request.createBy))
                {
                    whereConditions.Add("creator.Name LIKE @CreateByName");
                    parameters.Add("CreateByName", $"%{request.createBy}%");
                }

                // Фильтрация по дате создания (начало)
                if (!string.IsNullOrEmpty(request.createAtStartDate))
                {
                    whereConditions.Add("u.Create_At >= @CreateAtStart");
                    parameters.Add("CreateAtStart", request.createAtStartDate);
                }

                // Фильтрация по дате создания (конец)
                if (!string.IsNullOrEmpty(request.createAtEndDate))
                {
                    whereConditions.Add("u.Create_At <= @CreateAtEnd");
                    parameters.Add("CreateAtEnd", request.createAtEndDate);
                }

                // Фильтрация по дате последнего входа (начало)
                if (!string.IsNullOrEmpty(request.lastLoginDateStart))
                {
                    whereConditions.Add("ua.Last_Login_Date >= @LastLoginStart");
                    parameters.Add("LastLoginStart", request.lastLoginDateStart);
                }

                // Фильтрация по дате последнего входа (конец)
                if (!string.IsNullOrEmpty(request.lastLoginDateEnd))
                {
                    whereConditions.Add("ua.Last_Login_Date <= @LastLoginEnd");
                    parameters.Add("LastLoginEnd", request.lastLoginDateEnd);
                }

                string whereClause = whereConditions.Count > 0
                    ? $"WHERE {string.Join(" AND ", whereConditions)}"
                    : "";

                // Валидация и настройка сортировки
                var validSortColumns = new Dictionary<string, string>
                {
                    ["id"] = "u.ID",
                    ["name"] = "u.Name",
                    ["email"] = "u.Email",
                    ["phone"] = "u.Phone",
                    ["createat"] = "u.Create_At",
                    ["lastlogindate"] = "ua.Last_Login_Date", // сортировка по дате последнего входа
                    ["rolename"] = "r.Name", // сортировка по названию роли
                    ["creatorname"] = "creator.Name" // сортировка по имени создателя
                };

                var sortColumnKey = request.sortBy?.ToLower();
                string sortColumn = "u.Create_At";

                if (sortColumnKey is not null)
                {
                    sortColumn = validSortColumns.ContainsKey(sortColumnKey)
                    ? validSortColumns[sortColumnKey]
                    : "u.ID";
                }

                string orderByClause;

                // Обработка NULL значений для даты последнего логина
                // Если сортируем по lastlogindate, то NULL значения должны идти в конце
                if (sortColumnKey == "lastlogindate")
                {
                    if (request.sortType?.ToUpper() == "ASC")
                    {
                        orderByClause = $"ORDER BY ua.Last_Login_Date IS NULL, ua.Last_Login_Date ASC";
                    }
                    else
                    {
                        orderByClause = $"ORDER BY ua.Last_Login_Date IS NOT NULL, ua.Last_Login_Date DESC";
                    }
                }
                else
                {
                    orderByClause = $"ORDER BY {sortColumn} {request.sortType ?? "DESC"}";
                }

                // SQL для получения общего количества записей
                var countSql = $@"
                    SELECT COUNT(*)
                    FROM USER u 
                    INNER JOIN ROLE r ON u.Role_ID = r.ID
                    LEFT JOIN USER creator ON u.Create_By = creator.ID
                    LEFT JOIN (
                        SELECT User_ID, MAX(Login_Date) as Last_Login_Date
                        FROM USER_AUDIT
                        GROUP BY User_ID
                    ) ua ON u.ID = ua.User_ID
                    {whereClause}";

                int totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);
                int totalPages = (int)Math.Ceiling(totalCount / (double)request.pageSize);

                // SQL для получения данных с пагинацией
                var sql = $@"
                    SELECT 
                        u.ID, 
                        u.Name, 
                        u.Email, 
                        u.Phone, 
                        u.Password, 
                        u.Is_Deleted as isDeleted, 
                        DATE_FORMAT(u.Create_At, '%Y-%m-%dT%H:%i:%s') as CreateAt, 
                        r.ID, 
                        r.Name, 
                        r.Code, 
                        r.Rank, 
                        r.Actual,
                        creator.ID, 
                        creator.Name as creatorName, 
                        creator.Email as email,
                        ua.Last_Login_Date as lastLoginDate
                    FROM USER u 
                    INNER JOIN ROLE r ON u.Role_ID = r.ID
                    LEFT JOIN USER creator ON u.Create_By = creator.ID
                    LEFT JOIN (
                        SELECT User_ID, MAX(Login_Date) as Last_Login_Date
                        FROM USER_AUDIT
                        GROUP BY User_ID
                    ) ua ON u.ID = ua.User_ID
                    {whereClause}
                    {orderByClause}
                    LIMIT @PageSize OFFSET @Offset";

                parameters.Add("PageSize", request.pageSize);
                parameters.Add("Offset", offset);

                // Используем правильный тип для lastLoginDate и правильный splitOn
                var users = (await connection.QueryAsync<User, Role, User, DateTime?, UserResponse>(
                    sql,
                    (user, role, creator, lastLoginDate) =>
                    {
                        return new UserResponse(
                            user.id,
                            user.name,
                            user.email,
                            user.phone,
                            role,
                            user.isDeleted,
                            user.createAt,
                            creator?.id ?? 0,
                            creator?.name,
                            lastLoginDate?.ToString("yyyy-MM-ddTHH:mm:ss") // Преобразуем DateTime в строку
                        );
                    },
                    parameters,
                    splitOn: "ID,lastLoginDate" // разделяем User, Role, Creator и LastLoginDate
                )).ToList();

                return new UsersResponse(
                    users,
                    totalCount,
                    request.pageNumber,
                    request.pageSize,
                    totalPages
                );
            }
        }

        internal async Task<bool> UpdateUser(long userId, UserUpdateRequest request, string token)
        {
            string[] roles = { "ADMIN" }; // Роли, которые могут обновлять пользователей

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                long checkGrantsResult = await CheckGrants(connection, token, roles);

                // Проверка существования пользователя
                var userExistsSql = "SELECT COUNT(*) FROM USER WHERE ID = @UserId AND Is_Deleted = FALSE";
                var userExists = await connection.ExecuteScalarAsync<int>(userExistsSql, new { UserId = userId });

                if (userExists == 0)
                    throw new ArgumentException($"Пользователь не найден по заданному Id = {userId}");

                // Получаем ID роли по коду, если указана роль
                long? roleId = null;
                if (!string.IsNullOrEmpty(request.role))
                {
                    var roleSql = "SELECT ID FROM ROLE WHERE Code = @Code AND Actual = TRUE";
                    roleId = await connection.QueryFirstOrDefaultAsync<long?>(roleSql, new { Code = request.role });

                    if (roleId == null)
                        throw new ArgumentException($"Роль с кодом '{request.role}' не найдена");
                }

                // Динамическое построение SQL запроса
                var updateFields = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("UserId", userId);

                if (!string.IsNullOrEmpty(request.name))
                {
                    updateFields.Add("Name = @Name");
                    parameters.Add("Name", request.name);
                }

                if (!string.IsNullOrEmpty(request.email))
                {
                    updateFields.Add("Email = @Email");
                    parameters.Add("Email", request.email);
                }

                if (request.phone is not null)
                {
                    if (request.phone.Equals(""))
                    {
                        updateFields.Add("Phone = NULL");
                    }
                    else
                    {
                        updateFields.Add("Phone = @Phone");
                        parameters.Add("Phone", request.phone);
                    }
                }


                if (!string.IsNullOrEmpty(request.password))
                {
                    var hashedPassword = HashPassword(request.password);
                    updateFields.Add("Password = @Password");
                    parameters.Add("Password", hashedPassword);
                }

                if (roleId.HasValue)
                {
                    updateFields.Add("Role_ID = @RoleId");
                    parameters.Add("RoleId", roleId.Value);
                }

                // Если не указано ни одного поля для обновления
                if (updateFields.Count == 0)
                    return true;

                var sql = $@"
                    UPDATE USER 
                    SET {string.Join(", ", updateFields)}
                    WHERE ID = @UserId AND Is_Deleted = FALSE";

                int affectedRows = await connection.ExecuteAsync(sql, parameters);
                return affectedRows > 0;
            }
        }

        internal async Task<long> CreateUser(UserCreateRequest request, string token)
        {
            string[] roles = { "ADMIN" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                long checkGrantsResult = await CheckGrants(connection, token, roles);

                // Проверка уникальности email
                var emailExistsSql = "SELECT COUNT(*) FROM USER WHERE Email = @Email AND Is_Deleted = FALSE";
                var emailExists = await connection.ExecuteScalarAsync<int>(emailExistsSql, new { Email = request.email });

                if (emailExists > 0)
                    throw new Exception("Email уже используется");

                // Получаем ID роли по коду
                var roleSql = "SELECT ID FROM ROLE WHERE Code = @Code AND Actual = TRUE";
                var roleId = await connection.QueryFirstOrDefaultAsync<long?>(roleSql, new { Code = request.role });

                if (roleId == null)
                    throw new Exception($"Роль с кодом '{request.role}' не найдена");

                // Хеширование пароля
                var hashedPassword = HashPassword(request.password);

                // SQL для создания пользователя
                var sql = @"
                    INSERT INTO USER (Name, Email, Phone, Password, Role_ID, Create_By)
                    VALUES (@Name, @Email, @Phone, @Password, @RoleId, @CreateBy);
                    SELECT LAST_INSERT_ID();";

                var currentUser = await authorizationService.GetUserByToken(connection, token);

                var parameters = new
                {
                    Name = request.name,
                    Email = request.email,
                    Phone = request?.phone,
                    Password = hashedPassword,
                    RoleId = roleId.Value,
                    CreateBy = currentUser.id // ID текущего пользователя, который создает
                };

                // Выполняем вставку и получаем ID нового пользователя
                var newUserId = await connection.ExecuteScalarAsync<long>(sql, parameters);

                return newUserId;
            }
        }

        internal async Task<long> DeleteUser(long userId, string token)
        {
            string[] roles = { "ADMIN" };

            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                long checkGrantsResult = await CheckGrants(connection, token, roles);

                // Проверяем существование пользователя
                var userExistsSql = "SELECT COUNT(*) FROM USER WHERE ID = @UserId AND Is_Deleted = FALSE";
                var userExists = await connection.ExecuteScalarAsync<int>(userExistsSql, new { UserId = userId });

                if (userExists == 0)
                    throw new Exception("Пользователь не найден или уже удален");

                // Проверяем, не пытается ли пользователь удалить самого себя
                var currentUser = await authorizationService.GetUserByToken(connection, token);
                if (userId == currentUser.id)
                    throw new Exception("Нельзя удалить самого себя");

                // Выполняем soft delete (помечаем как удаленного)
                var deleteSql = @"
                    UPDATE USER 
                    SET Is_Deleted = TRUE,
                        Token = NULL
                    WHERE ID = @UserId AND Is_Deleted = FALSE";

                int affectedRows = await connection.ExecuteAsync(deleteSql, new { UserId = userId });

                if (affectedRows > 0)
                    return userId;
                else
                    return -1;
            }
        }
    }
}
