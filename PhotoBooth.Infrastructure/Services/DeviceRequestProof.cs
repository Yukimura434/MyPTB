using System;
using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Infrastructure.Services
{
    [DataContract]
    internal sealed class DevicePublicKey
    {
        [DataMember(Name="kty")] public string KeyType { get; set; } = "EC";
        [DataMember(Name="crv")] public string Curve { get; set; } = "P-256";
        [DataMember(Name="x")] public string X { get; set; }
        [DataMember(Name="y")] public string Y { get; set; }
    }

    internal sealed class DevicePublicKeyMaterial
    {
        public DevicePublicKey PublicKey { get; set; }
        public string Thumbprint { get; set; }
    }

    internal static class DeviceRequestProof
    {
        const int KeyVersion = 1;
        static readonly object KeyGate = new object();
        static readonly CngProvider SoftwareProvider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

        public static DevicePublicKeyMaterial GetOrCreatePublicKey(string installationId, bool allowCreation = true)
        {
            if(string.IsNullOrWhiteSpace(installationId))throw new InvalidOperationException("Không thể nhận diện kho khóa của thiết bị.");
            using(var key=allowCreation ? OpenOrCreate(installationId) : CngKey.Open("MiuPhotoBooth.Device."+Sha256Hex(Encoding.UTF8.GetBytes(installationId)),SoftwareProvider))
            {
                var blob=key.Export(CngKeyBlobFormat.EccPublicBlob);
                if(blob.Length<72)throw new CryptographicException("Khóa thiết bị không hợp lệ.");
                var size=BitConverter.ToInt32(blob,4);
                if(size!=32||blob.Length!=8+size*2)throw new CryptographicException("Khóa thiết bị không dùng P-256.");
                var x=new byte[size];var y=new byte[size];Buffer.BlockCopy(blob,8,x,0,size);Buffer.BlockCopy(blob,8+size,y,0,size);
                var publicKey=new DevicePublicKey{X=Base64Url(x),Y=Base64Url(y)};
                return new DevicePublicKeyMaterial{PublicKey=publicKey,Thumbprint=Sha256Hex(Encoding.UTF8.GetBytes(SerializePublicKey(publicKey)))};
            }
        }

        public static string SignActivation(string installationId,string attemptId,string challenge,string organizationId,string publicKeyThumbprint,string locationId,string deviceName,string platform,string appVersion)
        {
            var canonical=string.Join("\n",new[]{"photobooth-device-activation-v1",attemptId,challenge,organizationId,publicKeyThumbprint,locationId,deviceName,platform,appVersion??string.Empty});
            return Sign(installationId,Encoding.UTF8.GetBytes(canonical));
        }

        public static async Task SignAsync(HttpRequestMessage request,IDeviceIdentityRepository identities,string credential,string knownContentSha256,CancellationToken token)
        {
            if(request==null)throw new ArgumentNullException(nameof(request));
            var identity=await identities.GetAsync(token).ConfigureAwait(false);
            if(identity==null||string.IsNullOrWhiteSpace(identity.DeviceId)||string.IsNullOrWhiteSpace(identity.InstallationId))throw new InvalidOperationException("Thiết bị chưa có danh tính bảo mật.");
            var credentialId=CredentialId(credential);
            var contentSha256=knownContentSha256;
            if(string.IsNullOrWhiteSpace(contentSha256))
            {
                var bytes=request.Content==null?new byte[0]:await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();contentSha256=Sha256Hex(bytes);
            }
            contentSha256=contentSha256.Trim().ToLowerInvariant();
            if(contentSha256.Length!=64)throw new InvalidOperationException("Mã kiểm tra nội dung request không hợp lệ.");
            var timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce=RandomNonce();
            var pathAndQuery=request.RequestUri.PathAndQuery;
            var canonical=string.Join("\n",new[]{"photobooth-device-request-v1",credentialId,identity.DeviceId,request.Method.Method.ToUpperInvariant(),pathAndQuery,timestamp.ToString(CultureInfo.InvariantCulture),nonce,contentSha256,KeyVersion.ToString(CultureInfo.InvariantCulture)});
            request.Headers.TryAddWithoutValidation("X-Device-Content-Sha256",contentSha256);
            request.Headers.TryAddWithoutValidation("X-Device-Timestamp",timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-Device-Nonce",nonce);
            request.Headers.TryAddWithoutValidation("X-Device-Key-Version",KeyVersion.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-Device-Signature",Sign(identity.InstallationId,Encoding.UTF8.GetBytes(canonical)));
        }

        internal static string SerializePublicKey(DevicePublicKey value) => "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\""+value.X+"\",\"y\":\""+value.Y+"\"}";

        static CngKey OpenOrCreate(string installationId)
        {
            var name="MiuPhotoBooth.Device."+Sha256Hex(Encoding.UTF8.GetBytes(installationId));
            lock(KeyGate)
            {
                try{return CngKey.Open(name,SoftwareProvider);}
                catch(CryptographicException){}
                return CngKey.Create(CngAlgorithm.ECDsaP256,name,new CngKeyCreationParameters{Provider=SoftwareProvider,ExportPolicy=CngExportPolicies.None,KeyUsage=CngKeyUsages.Signing,KeyCreationOptions=CngKeyCreationOptions.None});
            }
        }

        static string Sign(string installationId,byte[] data)
        {
            // An enrolled identity must never silently replace its bound private key.
            var name="MiuPhotoBooth.Device."+Sha256Hex(Encoding.UTF8.GetBytes(installationId));
            using(var key=CngKey.Open(name,SoftwareProvider))using(var signer=new ECDsaCng(key))return Base64Url(signer.SignData(data,HashAlgorithmName.SHA256));
        }

        static string CredentialId(string credential)
        {
            var parts=(credential??string.Empty).Split('.');
            if(parts.Length!=3||parts[0]!="dv3"||string.IsNullOrWhiteSpace(parts[1]))throw new InvalidOperationException("Thông tin xác thực thiết bị không hợp lệ.");
            return parts[1];
        }

        static string RandomNonce(){var bytes=new byte[24];using(var random=RandomNumberGenerator.Create())random.GetBytes(bytes);return Base64Url(bytes);}
        static string Sha256Hex(byte[] value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(value)).Replace("-",string.Empty).ToLowerInvariant();}
        static string Base64Url(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    }
}
