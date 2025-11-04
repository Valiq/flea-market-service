namespace flea_market_service.Models
{
    public class Offer
    {
        public long id { get; set; }
        public string name { get; set; }
        public string? description { get; set; }
        public OfferImage? image { get; set; }
        public Seller seller { get; set; }
        public decimal price { get; set; }
        public OfferStatus status { get; set; }
        public string? lastPushing { get; set; }
        public string createAt { get; set; }
        public string? changeAt { get; set; }
        public List<Tag>? tags { get; set; }
        public string lastModerationStatus { get; set; }
    }
}
