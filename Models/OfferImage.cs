namespace flea_market_service.Models
{
    public class OfferImage
    {
        public long id { get; set; }
        public string name { get; set; }
        public string path { get; set; }
        public string extension { get; set; }
        public int size { get; set; }
        public string createAt { get; set; }
    }
}
