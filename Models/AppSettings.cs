namespace flea_market_service.Models
{
    public class AppSettings
    {
        public long id { get; set; }
        public long sellerPushingInterval { get; set; }
        public int maxSellerOffers { get; set; }
        public int maxSellerFilters { get; set; }
        public long offerLifeTime { get; set; }
        public int minOfferScore { get; set; }
        public int minFilterScore { get; set; }
        public int compliancePercentage { get; set; }
    }
}
