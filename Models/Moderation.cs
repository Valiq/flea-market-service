namespace flea_market_service.Models
{
    public class Moderation
    {
        public long id { get; set; }
        public OfferAudit offerAudit { get; set; }
        public ModerationStatus moderationStatus { get; set; }
        public string? comment { get; set; }
        public string? changeAt { get; set; }
        public User? changeBy { get; set; }
        public string createAt { get; set; }
    }
}
