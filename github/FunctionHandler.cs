using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace Function
{
    public class FunctionHandler
    {
        public async Task<(int, string)> Handle(HttpRequest request)
        {
            var context = new GitHubContext(request, "GitHubApp");

            context.Verify();

            if (context.IsEvent("issues") && context.IsAction("opened"))
            {
                // get the values from the payload data
                var issueNumber = (int)context.Data["issue"]["number"];
                var installationId = (int)context.Data["installation"]["id"];
                var owner = (string)context.Data["repository"]["owner"]["login"];
                var repo = (string)context.Data["repository"]["name"];

                var installationClient = await context.GetInstallationClient(installationId);

                // add a comment to the issue
                var issueComment = await installationClient.Issue.Comment.Create(owner, repo, issueNumber, "Hello from my GitHubApp Installation!");

                return (200, "Success");
            }

            return (200, "No Action");
        }
    }
}