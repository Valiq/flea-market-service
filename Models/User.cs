namespace flea_market_service.Models
{
    public class User
    {
        public long id { get; set; }
        public string name { get; set; }
        public string email { get; set; }
        public string phone { get; set; }
        public string password { get; set; }
        public Role role { get; set; }
        public string token { get; set; }
        public bool isDeleted { get; set; }
        public string createAt { get; set; }
        public long createBy { get; set; }
    }
}
