namespace flea_market_service.Models
{
    public class Seller
    {
        public long id { get; set; }
        public string telegramId { get; set; }
        public string managerChatId { get; set; }
        public string deliveryChatId { get; set; }
        public string name { get; set; }
        public bool isActive {  get; set; }
        public string createAt { get; set; }
    }
}
