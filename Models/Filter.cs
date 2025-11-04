namespace flea_market_service.Models
{
    public class Filter
    {
        public long id { get; set; }
        public string name { get; set; }
        public Seller seller { get; set; }
        public bool isActive { get; set; }
        public string createAt { get; set; }
        public List<Tag> tags { get; set; }
    }
}
