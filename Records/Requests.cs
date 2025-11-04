using flea_market_service.Models;
using System.ComponentModel.DataAnnotations;

namespace flea_market_service.Records
{
    public class Requests
    {
        public record OfferRequest(
            string? name,
            long? sellerId,
            string? sellerName,
            double? startPrice,
            double? endPrice,
            string[]? status,
            string[]? tags,
            string? lastPushingStartDate,
            string? lastPushingEndDate,
            string? createAtStartDate,
            string? createAtEndDate,
            string? changeAtStartDate,
            string? changeAtEndDate,

            string? sortBy,
            string? sortType,

            [Required]
            int pageNumber,

            [Required]
            int pageSize
        );

        public record OfferStatusRequest(
            [Required]
            string status,

            [Required]
            List<long> ids
        );

        public record SellerRequest(
            long? telegramId,
            string? name,
            bool? isActive,
            string? createAtStartDate,
            string? createAtEndDate,

            string? sortBy,
            string? sortType,

            [Required]
            int pageNumber,

            [Required]
            int pageSize
        );

        public record FilterCreateRequest(
            string sellerId,
            string chatId,
            string chatType,
            string sellerName,
            string name,
            string[] tags
        );

        public record FilterUpdateRequest(
            string? name,
            bool? isActive,
            string[]? tags
        );

        public record ModerationRequest(
            string[]? ModerationStatus,
            string? name,
            string[]? actionType,
            long? sellerId,
            string? sellerName,
            double? startPrice,
            double? endPrice,
            string[]? tags,
            string? offerId,
            string? createAtStartDate,
            string? createAtEndDate,
            string? changeByName,
            string? changeAtStartDate,
            string? changeAtEndDate,

            string? sortBy,
            string sortType,

            [Required]
            int pageNumber,

            [Required]
            int pageSize
        );

        public record ModerationCreateRequest(
            string name,
            string? description,
            string actionType,
            string sellerId,
            string chatId,
            string chatType,
            string sellerName,
            double price,
            string[] tags,
            long? imageId,
            long? offerId
        );

        public record ModerationUpdateRequest(
            string? status,

            [MaxLength(5000)]
            string? comment
        );

        public record TagRequest(
            string? name,
            bool isActive,
            int startOfferScore,
            int endOfferScore,
            int startFilterScore,
            int endFilterScore,
            string createAtStartDate,
            string createAtEndDate,

            string sortBy,

            [Required]
            int pageNumber,

            [Required]
            int pageSize
        );

        public record TagUpdateRequest(
            string status,

            [Required]
            List<long> ids
        );

        public record TagDeleteRequest(
            [Required]
            List<long> ids
        );

        public record LoginRequest(
           [Required]
           [EmailAddress]
           [MaxLength(255)]
           string email,

           [Required]
           [MinLength(6)]
           [MaxLength(50)]
           string password
        );

        public record UserRequest(
            [EmailAddress]
            string? email,
            string? name,
            string? phone,
            string[]? role,
            bool? isDeleted,
            string? createAtStartDate,
            string? createAtEndDate,
            string? createBy,
            string? lastLoginDateStart,
            string? lastLoginDateEnd,

            string? sortBy,
            string? sortType,

            [Required]
            int pageNumber,

            [Required]
            int pageSize
        );

        public record UserCreateRequest(
            [Required]
            [MinLength(3)]
            [MaxLength(100)]
            string name,

            [Required]
            [EmailAddress]
            [MaxLength(255)]
            string email,

            [MaxLength(20)]
            string? phone,

            [Required]
            [MinLength(8)]
            [MaxLength(255)]
            string password,

            [Required]
            string role
        );

        public record UserUpdateRequest(
            [MinLength(3)]
            [MaxLength(100)]
            string? name,

            [EmailAddress]
            [MaxLength(255)]
            string? email,

            [MaxLength(20)]
            string? phone,

            [MinLength(8)]
            [MaxLength(255)]
            string? password,

            string? role
        );

        public record AppSettingsRequest(
            long? sellerPushingInterval,
            int? maxSellerOffers,
            int? maxSellerFilters,
            long? offerLifeTime,
            int? minOfferScore,
            int? minFilterScore,
            int? compliancePercentage
        );
    }
}
