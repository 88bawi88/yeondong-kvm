// 릴리스 서명 도구. 개인키는 내보낼 수 없는 CNG 키로 현재 사용자 저장소에 둔다.
//   sign keygen <키이름>          키를 만들고(있으면 그대로) 공개키 base64를 출력
//   sign pub <키이름>             공개키 base64 출력
//   sign file <키이름> <파일>     <파일>.sig 를 만든다
//   sign verify <공개키> <파일>   <파일>.sig 를 확인. 종료 코드 0=OK 1=FAIL
//   sign delete <키이름>          키 삭제(시험용)
using System;
using System.IO;
using System.Security.Cryptography;

static class SignTool
{
    static int Main(string[] a)
    {
        try
        {
            if (a.Length < 2) return Usage();
            switch (a[0])
            {
                case "keygen":
                    {
                        CngKey k;
                        if (CngKey.Exists(a[1])) k = CngKey.Open(a[1]);
                        else k = CngKey.Create(CngAlgorithm.ECDsaP256, a[1], new CngKeyCreationParameters
                        {
                            ExportPolicy = CngExportPolicies.None,
                            KeyCreationOptions = CngKeyCreationOptions.None
                        });
                        using (k) Console.WriteLine(Pub(k));
                        return 0;
                    }
                case "pub":
                    using (var k = CngKey.Open(a[1])) Console.WriteLine(Pub(k));
                    return 0;
                case "file":
                    {
                        if (a.Length < 3) return Usage();
                        byte[] data = File.ReadAllBytes(a[2]);
                        using (var k = CngKey.Open(a[1]))
                        using (var ec = new ECDsaCng(k) { HashAlgorithm = CngAlgorithm.Sha256 })
                            File.WriteAllBytes(a[2] + ".sig", ec.SignData(data));
                        Console.WriteLine("wrote " + a[2] + ".sig");
                        return 0;
                    }
                case "verify":
                    {
                        if (a.Length < 3) return Usage();
                        byte[] data = File.ReadAllBytes(a[2]), sig = File.ReadAllBytes(a[2] + ".sig");
                        bool ok;
                        using (var k = CngKey.Import(Convert.FromBase64String(a[1]), CngKeyBlobFormat.EccPublicBlob))
                        using (var ec = new ECDsaCng(k) { HashAlgorithm = CngAlgorithm.Sha256 })
                            ok = ec.VerifyData(data, sig);
                        Console.WriteLine(ok ? "OK" : "FAIL");
                        return ok ? 0 : 1;
                    }
                case "delete":
                    using (var k = CngKey.Open(a[1])) k.Delete();
                    Console.WriteLine("deleted " + a[1]);
                    return 0;
            }
            return Usage();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 2;
        }
    }

    static string Pub(CngKey k)
    {
        return Convert.ToBase64String(k.Export(CngKeyBlobFormat.EccPublicBlob));
    }

    static int Usage()
    {
        Console.Error.WriteLine("sign keygen|pub|delete <keyName> | sign file <keyName> <path> | sign verify <pubBase64> <path>");
        return 2;
    }
}
