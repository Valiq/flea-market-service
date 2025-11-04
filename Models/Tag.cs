namespace flea_market_service.Models
{
    public class Tag
    {
        public long id { get; set; }
        public string name { get; set; }
        public bool isActive {  get; set; }
        public int offerScore { get; set; }
        public int filterScore { get; set; }
        public string createAt { get; set; }
    }
}
