using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;

namespace MyWebApi.Controllers
{
    /// <summary>
    /// Manages user registration for the application
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Produces("application/json")]
    public class AuthController : ControllerBase
    {
        /// <summary>
        /// Dictionnary of users and their generated secrets
        /// </summary>
        /// <typeparam name="string">Username</typeparam>
        /// <typeparam name="string">Password</typeparam>
        public static ConcurrentDictionary<string, string> users = new ConcurrentDictionary<string, string>();

        /// GET api/auth
        /// <summary>
        /// List registered users
        /// </summary>
        /// <returns>The list of users registered</returns>
        [HttpGet]
        [ProducesResponseType(typeof(ICollection<String>), 200)]
        public ICollection<string> Get()
        {
            return users.Keys;
        }

        // GET api/auth/{username}
        /// <summary>
        /// Checks wether a username is registered
        /// </summary>
        /// <param name="username">the user you are looking for</param>
        /// <returns>204 if it exists, 404 otherwise</returns>
        [HttpGet("{username}")]
        [ProducesResponseType(typeof(string), 200)]
        [ProducesResponseType(404)]
        public ActionResult<string> Get(string username)
        {
            if (users.Keys.Contains(username))
                return NoContent();

            return NotFound();
        }

        // POST api/auth
        /// <summary>
        /// Registers a given username
        /// </summary>
        /// <param name="username">JSON string containing the desired nickname</param>
        /// <returns>The password the user will need to connect via websocket</returns>
        [HttpPost]
        [ProducesResponseType(typeof(string), 200)]
        [ProducesResponseType(400)]
        public ActionResult<string> Post([FromBody] string username)
        {
            if (username.Contains(':'))
                return BadRequest("Username cannot contain ':'");

            var secret = Guid.NewGuid().ToString();
            if (users.TryAdd(username, secret))
                return Ok(secret);

            return BadRequest("Username already in use");
        }

    }
}
