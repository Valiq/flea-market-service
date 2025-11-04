using Dapper;
using flea_market_service.Models;
using MySqlConnector;
using System.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using System.Diagnostics;

namespace flea_market_service.Core.Services
{
    public class OfferImageService
    {
        protected readonly IConfiguration Configuration;

        internal OfferImageService(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        internal async Task<long> SaveTelegramImage(Stream imageStream, long sellerId, string originalFileName)
        {
            long maxSizeBytes = 5 * 1024 * 1024;
            int thumbnailWidth = 300;
            int thumbnailHeight = 300;
            long imageId = 0;

            var imagesDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Images");
            var thumbnailsDirectory = Path.Combine(imagesDirectory, "Thumbnails");

            try
            {
                // Проверка размера файла
                if (imageStream.Length > maxSizeBytes)
                    throw new Exception($"Размер файла превышает допустимый лимит: {maxSizeBytes / 1024 / 1024}MB");

                imageStream.Seek(0, SeekOrigin.Begin);

                // Пытаемся определить формат изображения
                var imageFormat = await Image.DetectFormatAsync(imageStream);
                if (imageFormat == null)
                    throw new Exception("Файл не является поддерживаемым изображением");

                imageStream.Seek(0, SeekOrigin.Begin);

                var image = await Image.LoadAsync(imageStream);

                if (!Directory.Exists(imagesDirectory))
                    Directory.CreateDirectory(imagesDirectory);

                if (!Directory.Exists(thumbnailsDirectory))
                    Directory.CreateDirectory(thumbnailsDirectory);

                imageId = await CreateOfferImageRecord(new OfferImage
                {
                    name = null,
                    path = null,
                    extension = "png",
                    size = (int)imageStream.Length
                });

                var mainFileName = $"sellerId_{sellerId}_imgId_{imageId}";
                var thumbnailFileName = $"sellerId_{sellerId}_imgId_{imageId}_thumb";

                var mainPath = Path.Combine(imagesDirectory, mainFileName);
                var thumbnailPath = Path.Combine(thumbnailsDirectory, thumbnailFileName);

                await UpdateOfferImagePathAndName(imageId, imagesDirectory, mainFileName);

                await image.SaveAsPngAsync($"{mainPath}.png");

                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(thumbnailWidth, thumbnailHeight),
                    Mode = ResizeMode.Max,
                    Compand = true
                }));

                await image.SaveAsPngAsync($"{thumbnailPath}.png");

                return imageId;
            }
            catch (Exception ex)
            {
                // Удаляем частично сохраненные файлы в случае ошибки
                var imagePattern = $"sellerId_{sellerId}_imgId_{imageId}*";

                foreach (var file in Directory.GetFiles(imagesDirectory, imagePattern))
                {
                    File.Delete(file);
                }

                Debug.WriteLine($"\nОшибка при сохранении изображения: {ex.Message}");

                return -1;
            }
        }

        private async Task<long> CreateOfferImageRecord(OfferImage newImage)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                var sql = @"
                        INSERT INTO OFFER_IMAGE 
                            (Name, Path, Extension, Size)
                        VALUES 
                            (@Name, @Path, @Extension, @Size);
                        SELECT LAST_INSERT_ID();";

                var parameters = new
                {
                    Name = newImage.name,
                    Path = newImage.path,
                    Extension = newImage.extension,
                    Size = newImage.size,
                };

                var imageId = await connection.ExecuteScalarAsync<long>(sql, parameters);

                // Получаем полную информацию
                var selectSql = @"
                        SELECT 
                            ID as id,
                            Name as name,
                            Path as path,
                            Extension as extension,
                            Size as size,
                            Create_At as createAt
                        FROM OFFER_IMAGE 
                        WHERE ID = @ImageId";

                var image = await connection.QueryFirstOrDefaultAsync<OfferImage>(
                    selectSql, new { ImageId = imageId });

                return image.id;
            }
        }

        internal async Task<bool> UpdateOfferImagePathAndName(long imageId, string newPath, string newName)
        {
            using (MySqlConnection connection = new MySqlConnection(Configuration.GetValue<string>("ConnectionString")))
            {
                await connection.OpenAsync();

                var sql = @"
                    UPDATE OFFER_IMAGE 
                    SET 
                        Name = @Name,
                        Path = @Path
                    WHERE ID = @ImageId";

                var parameters = new
                {
                    ImageId = imageId,
                    Name = newName,
                    Path = newPath
                };

                int affectedRows = await connection.ExecuteAsync(sql, parameters);
                return affectedRows > 0;
            }
        }
    }
}
