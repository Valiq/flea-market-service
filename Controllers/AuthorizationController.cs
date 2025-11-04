using flea_market_service.Core.Services;
using Microsoft.AspNetCore.Mvc;
using static flea_market_service.Records.Requests;
using static flea_market_service.Records.Respons;

namespace flea_market_service.Controllers
{
    public class AuthorizationController : Controller
    {
        private readonly AuthorizationService service;

        public AuthorizationController(IConfiguration configuration)
        {
            service = new AuthorizationService(configuration);
        }

        [HttpPost("api/v1/session")]
        public async Task<IActionResult> CreateSession([FromBody] LoginRequest request)
        {
            try
            {
                string token = await service.CreateSession(request);

                return Ok(new { Token = token });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("api/v1/sessions/user")]
        public async Task<IActionResult> GetUserByToken([FromHeader] string Token)
        {
            try
            {
                UserResponse user = await service.GetUserByToken(Token);

                return Ok(user);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
