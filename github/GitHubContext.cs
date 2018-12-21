using GitHubJwt;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Octokit;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Function
{
    public class GitHubContext
    {
        const string Header_XHubSignature = "X-Hub-Signature";
        const string Header_XGitHubEvent = "X-GitHub-Event";
        const string EnvVariable_GitHubSecretKey = "GH_SecretKey";
        const string EnvVariable_GitHubApplicationId = "GH_ApplicationId";
        const string EnvVariable_GitHubPrivateKey = "GH_PrivateKey";

        readonly string appName;
        readonly HttpRequest request;
        readonly Lazy<string> input;
        readonly Lazy<JObject> data;

        public GitHubContext(HttpRequest request, string appName)
        {
            this.appName = appName;
            this.request = request;
            input = new Lazy<string>(() => new StreamReader(request.Body).ReadToEnd());
            data = new Lazy<JObject>(() => JObject.Parse(input.Value));
        }

        public JObject Data => data.Value;

        public void Verify()
        {
            // 1. Get the expected hash from the signature header.
            var header = (string)request.Headers[Header_XHubSignature];

            var values = header.Split('=').Select(v => v.Trim()).ToArray();

            if (values.Length != 2 || values[0] != "sha1")
                throw new Exception($"Incorrect Signature Header - '{header}'");

            var expectedHash = FromHex(values[1]);

            // 2. Get the configured secret key.
            var secretKey = Environment.GetEnvironmentVariable(EnvVariable_GitHubSecretKey);
            var secret = Encoding.ASCII.GetBytes(secretKey);

            // 3. Get the actual hash of the request body.
            var actualHash = ComputeRequestBodySha1Hash(input.Value.Replace(Environment.NewLine, "\n"), secret);

            // 4. Verify that the actual hash matches the expected hash.
            if (!SecretEqual(expectedHash, actualHash))
                throw new Exception($"Signature does not match body - '{BitConverter.ToString(expectedHash).Replace("-", string.Empty)}' - '{BitConverter.ToString(actualHash).Replace("-", string.Empty)}'");
        }

        public bool IsEvent(string eventType) => string.Equals(request.Headers[Header_XGitHubEvent], eventType, StringComparison.InvariantCultureIgnoreCase);

        public bool IsAction(string action) => string.Equals((string)data.Value["action"], action, StringComparison.InvariantCultureIgnoreCase);

        public GitHubClient GetGitHubClient()
        {
            var jwtToken = GetJwtToken();

            var appClient = new GitHubClient(new ProductHeaderValue(appName))
            {
                Credentials = new Credentials(jwtToken, AuthenticationType.Bearer)
            };

            return appClient;
        }

        public async Task<GitHubClient> GetInstallationClient(int installationId)
        {
            var appClient = GetGitHubClient();

            // create an installation token en ensure we respond to the right GitHubApp installation
            var response = await appClient.GitHubApps.CreateInstallationToken(installationId).ConfigureAwait(false);

            // create a client with the installation token
            var installationClient = new GitHubClient(new ProductHeaderValue($"{appName}_{installationId}"))
            {
                Credentials = new Credentials(response.Token)
            };

            return installationClient;
        }

        static byte[] FromHex(string content)
        {
            if (string.IsNullOrEmpty(content))
                return Array.Empty<byte>();

            try
            {
                var data = new byte[content.Length / 2];
                var input = 0;
                for (var output = 0; output < data.Length; output++)
                    data[output] = Convert.ToByte(new string(new char[2] { content[input++], content[input++] }), 16);

                if (input != content.Length)
                    throw new Exception($"The signature length was not valid - '{content}'");

                return data;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is FormatException)
            {
                throw new Exception($"The signature format was not valid - '{content}'", exception);
            }
        }

        static byte[] ComputeRequestBodySha1Hash(string input, byte[] secret)
        {
            using (var hasher = new HMACSHA1(secret))
            {
                var buffer = Encoding.ASCII.GetBytes(input);

                hasher.TransformBlock(
                        buffer,
                        inputOffset: 0,
                        inputCount: buffer.Length,
                        outputBuffer: null,
                        outputOffset: 0);

                hasher.TransformFinalBlock(Array.Empty<byte>(), inputOffset: 0, inputCount: 0);

                return hasher.Hash;
            }
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        static bool SecretEqual(byte[] inputA, byte[] inputB)
        {
            if (ReferenceEquals(inputA, inputB))
                return true;
            if (inputA == null || inputB == null || inputA.Length != inputB.Length)
                return false;
            var areSame = true;
            for (var i = 0; i < inputA.Length; i++)
                areSame &= inputA[i] == inputB[i];
            return areSame;
        }

        static string GetJwtToken()
        {
            var appId = Environment.GetEnvironmentVariable(EnvVariable_GitHubApplicationId);
            var options = new GitHubJwtFactoryOptions
            {
                AppIntegrationId = int.Parse(appId), // The GitHub App Id
                ExpirationSeconds = 599 // 10 minutes is the maximum time allowed
            };
            var privateKey = Environment.GetEnvironmentVariable(EnvVariable_GitHubPrivateKey).Replace("\n", Environment.NewLine);
            var generator = new GitHubJwtFactory(new StringPrivateKeySource(privateKey), options);
            return generator.CreateEncodedJwtToken();
        }
    }
}