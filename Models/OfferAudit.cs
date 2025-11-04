namespace flea_market_service.Models
{
    public class OfferAudit
    {
        public long id { get; set; }
        public string name { get; set; }
        public string description { get; set; }
        public Seller seller { get; set; }
        public ActionType actionType { get; set; }
        public OfferImage? image { get; set; }
        public decimal price { get; set; }
        public string tags { get; set; }
        public long? offerId { get; set; }
        public string createAt { get; set; }
    }


    public class OfferAuditRecord
    {
        public long id { get; set; }
        public string name { get; set; }
        public string? description { get; set; }
        public long sellerId { get; set; }
        public long actionTypeId { get; set; }
        public long? imageId { get; set; }
        public decimal price { get; set; }
        public string? tags { get; set; }
        public long? offerId { get; set; }
        public string createAt { get; set; }
    }
}
