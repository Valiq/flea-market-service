using flea_market_service.Core.Services;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using static flea_market_service.Records.Requests;

namespace flea_market_service.Controllers
{
    [ApiController]
    public class UserController : Controller
    {
        private readonly UserService service;

        public UserController(IConfiguration configuration)
        {
            service = new UserService(configuration);
        }

        [HttpPost("api/v1/users")]
        public async Task<IActionResult> GetUsers([FromHeader] string Token, UserRequest request)
        {
            try
            {
                var result = await service.GetUsersPaged(request, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("api/v1/users/{id}")]
        public async Task<IActionResult> GetUser([FromHeader] string Token, [FromRoute] long id)
        {
            try
            {
                var result = await service.GetUserById(id, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("api/v1/user")]
        public async Task<IActionResult> CreateUser([FromHeader] string Token, [FromBody] UserCreateRequest request)
        {
            try
            {
                var result = await service.CreateUser(request, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPatch("api/v1/users/{id}")]
        public async Task<IActionResult> UpdateUser([FromHeader] string Token, [FromRoute] long id, [FromBody] UserUpdateRequest request)
        {
            try
            {
                var result = await service.UpdateUser(id, request, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpDelete("api/v1/users/{id}")]
        public async Task<IActionResult> DeleteUser([FromHeader] string Token, [FromRoute] long id)
        {
            try
            {
                var result = await service.DeleteUser(id, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
