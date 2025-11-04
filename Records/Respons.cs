using flea_market_service.Models;
using System.ComponentModel.DataAnnotations;

namespace flea_market_service.Records
{
    public class Respons
    {
        public record UserResponse(
            long id,
            string name,
            string email,
            string phone,
            Role role,
            bool isDeleted,
            string createAt,
            long creatorId,
            string creatorName,
            string lastLoginDate
        );

        public record UsersResponse(
            List<UserResponse> users,
            int totalCount,
            int pageNumber,
            int pageSize,
            int totalPages
        );

        public record ModerationsResponse(
            List<Moderation> moderations,
            int totalCount,
            int pageNumber,
            int pageSize,
            int totalPages
        );

        public record OffersResponse(
            List<Offer> offers,
            int totalCount,
            int pageNumber,
            int pageSize,
            int totalPages
        );

        public record SellersResponse(
            List<Seller> sellers,
            int totalCount,
            int pageNumber,
            int pageSize,
            int totalPages
        );

        public record TagsResponse(
            List<Tag> tags,
            int totalCount,
            int pageNumber,
            int pageSize,
            int totalPages
        );
    }
}
