namespace flea_market_service.Models
{
    public class UserAudit
    {
        public long id { get; set; }
        public User user { get; set; }
        public string loginDate { get; set; }
    }
}
