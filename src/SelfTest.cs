using System.Security.Cryptography;
using System.Text.Json;
namespace WishGuard;
public static class SelfTest
{
    static JsonSerializerOptions Json => new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static async Task<int> Analyze(string image, string report)
    {
        using var frame = new Bitmap(image); using var worker = new OcrWorker(); await worker.Start();
        var resources = await ResourceReader.Read(frame, worker);
        using var footer = ResourceReader.Crop(frame, Rectangle.FromLTRB(0,(int)(frame.Height*.68),frame.Width,frame.Height));
        bool wish = WishText.IsWish(await worker.Read(footer));
        File.WriteAllText(report, JsonSerializer.Serialize(new { wish, resources }, Json)); return wish && resources.Valid ? 0 : 1;
    }
    public static async Task<int> Run(string[] args)
    {
        var results = new List<object>(); int failed = 0;
        void Check(string name, Action action) { try { action(); results.Add(new { name, pass = true }); } catch(Exception e) { failed++; results.Add(new { name, pass = false, error = e.Message }); } }
        void Expect(bool value) { if (!value) throw new Exception("assertion failed"); }
        void Reject(Action action) { bool rejected=false; try { action(); } catch { rejected=true; } Expect(rejected); }
        string data = Program.GetArg(args,"--data") ?? Path.Combine(Path.GetTempPath(),"WishGuardTest-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(data);
        Check("600 pull boundary",()=>Expect(Rules.Pulls(95999,0)==599 && Rules.Pulls(96000,0)==600 && Rules.Pulls(80000,100)==600));
        Check("new screen stays covered until repeated clear frames",()=> { var gate=new ScreenGate();Expect(gate.Blocked);gate.Observe(gate.Version,true,false);Expect(gate.Blocked);gate.Observe(gate.Version,true,false);Expect(!gate.Blocked); });
        Check("wish detection covers immediately",()=> { var gate=new ScreenGate();gate.Observe(gate.Version,true,true);Expect(gate.Blocked&&gate.WishSeen); });
        Check("switching away and returning preserves wish history",()=> { var gate=new ScreenGate();gate.Observe(gate.Version,true,true);gate.Recheck();Expect(gate.Blocked&&gate.WishSeen);gate.Observe(gate.Version,true,false);gate.Observe(gate.Version,true,false);Expect(gate.Blocked);gate.Observe(gate.Version,true,false);Expect(!gate.Blocked); });
        Check("old asynchronous frames cannot remove new cover",()=> { var gate=new ScreenGate();int old=gate.Version;gate.Recheck();for(int i=0;i<5;i++)gate.Observe(old,true,false);Expect(gate.Blocked); });
        Check("unreadable frame resets safe-frame count",()=> { var gate=new ScreenGate();gate.Observe(gate.Version,true,false);gate.Observe(gate.Version,false,false);gate.Observe(gate.Version,true,false);Expect(gate.Blocked); });
        Check("entry transition cannot be cleared by earlier normal frames",()=> { var gate=new ScreenGate();int old=gate.Version;gate.HoldUntil(700);for(int i=0;i<4;i++){gate.Observe(old,true,false,800);gate.Observe(gate.Version,true,false,600);}Expect(gate.Blocked);gate.Observe(gate.Version,true,false,700);Expect(gate.Blocked);gate.Observe(gate.Version,true,false,701);Expect(!gate.Blocked); });
        async Task CheckReadiness(string name, string[] statuses, string expected, int expectedWaits)
        {
            int waits = 0, repairs = 0;
            string result = await ProtectionReadiness.Confirm(() => statuses[Math.Min(waits, statuses.Length - 1)], () => repairs++,
                () => { waits++; return Task.CompletedTask; });
            Check(name, () => Expect(result == expected && waits == expectedWaits && repairs == waits + 1));
        }
        await CheckReadiness("startup focus handoff recovers before reporting failure", ["focus-unavailable", "focus-unavailable", "focus-unavailable", "focus-unavailable", "ready"], "ready", 4);
        await CheckReadiness("persistent focus failure remains a failure", ["focus-unavailable"], "focus-unavailable", 5);
        await CheckReadiness("keyboard protection handshake settles", ["input-pending", "ready"], "ready", 1);
        await CheckReadiness("persistent keyboard protection failure remains a failure", ["input-pending"], "input-pending", 5);
        Check("Escape and desktop navigation remain available",()=>Expect(KeyboardPolicy.Allow(0x1B,false)&&KeyboardPolicy.Allow(0x5B,false)&&KeyboardPolicy.Allow(0x09,true)&&KeyboardPolicy.Allow(0x73,true)));
        Check("covered game confirmation keys are blocked",()=>Expect(!KeyboardPolicy.Allow(0x0D,false)&&!KeyboardPolicy.Allow(0x20,false)&&!KeyboardPolicy.Allow(0x46,false)));
        Check("releases reach game for keys held before cover",()=>Expect(KeyboardPolicy.Allow(0x57,false,false)&&KeyboardPolicy.Allow(0x26,false,false)));
        Check("first slower wish load stays covered until scene confirmation",()=> {var gate=new ScreenGate();gate.HoldUntil(5000);foreach(long time in new long[]{800,1600,2400,3200})gate.Observe(gate.Version,true,false,time);Expect(gate.Blocked);gate.Observe(gate.Version,true,true,3500);Expect(gate.Blocked&&gate.WishSeen);});
        Check("confirmed wish does not delay a subsequent return",()=> {var gate=new ScreenGate();gate.HoldUntil(5000);gate.Observe(gate.Version,true,true,1200);for(int i=0;i<3;i++)gate.Observe(gate.Version,true,false,1400+i);Expect(!gate.Blocked);});
        Check("early return cancels entry wait only after new clear frames",()=> {var gate=new ScreenGate();gate.HoldUntil(5000);int old=gate.Version;gate.CancelHold();gate.Observe(old,true,false,300);Expect(gate.Blocked);gate.Observe(gate.Version,true,false,400);Expect(gate.Blocked);gate.Observe(gate.Version,true,false,500);Expect(!gate.Blocked);});
        Check("return intent cannot clear a visible wish or unreadable scene",()=> {var gate=new ScreenGate();gate.HoldUntil(5000);gate.CancelHold();gate.Observe(gate.Version,true,true,300);Expect(gate.Blocked&&gate.WishSeen);gate.Observe(gate.Version,false,false,400);Expect(gate.Blocked);});
        Check("protected policy replacement tolerates live shared readers",()=> {
            string path=Path.Combine(data,"shared-profile.json");var profile=new EnforcementProfile();EnforcementPaths.Save(path,profile);
            using var reader=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            profile.Completed=true;EnforcementPaths.Save(path,profile);Expect(EnforcementPaths.Read(path).Completed);
            Expect(!System.Text.Json.JsonSerializer.Deserialize<EnforcementProfile>(reader)!.Completed);
        });
        await CheckReadiness("persistent missing cover remains a failure", ["cover-unavailable"], "cover-unavailable", 5);
        await CheckReadiness("window visibility and focus settle before health report", ["cover-unavailable", "focus-unavailable", "ready"], "ready", 2);
        await CheckReadiness("recognition failure during handoff is reported immediately", ["focus-unavailable", "recognition-fault"], "recognition-fault", 1);
        await CheckReadiness("disposal during handoff is reported immediately", ["focus-unavailable", "inactive"], "inactive", 1);
        Check("UI fonts survive repeated same-size layout and view changes",()=>UiRegression.VerifyFontLifetime(data));
        Check("first shown cover aligns without waiting for another frame",UiRegression.VerifyShieldInitialPlacement);
        Check("original resources stay visible while the lower wish area remains covered",UiRegression.VerifyResourceVisibility);
        Check("negative resources rejected",()=>Reject(()=>Rules.Pulls(-1,0)));
        Check("oversized resources rejected",()=>Reject(()=>Rules.Pulls(long.MaxValue,0)));
        Check("Chinese OCR whitespace",()=>Expect(WishText.IsWish("祈 愿 1 0 次")));
        Check("single wish",()=>Expect(WishText.IsWish("祈愿1次")));
        Check("fullwidth digits",()=>Expect(WishText.IsWish("祈願１０次")));
        Check("ordinary mention ignored",()=>Expect(!WishText.IsWish("任务：前往祈愿之地")));
        Check("blank ignored",()=>Expect(!WishText.IsWish("")));
        Check("startup loading text is not playable HUD",()=>Expect(!SceneText.HasGameHud("准备加载数据 45.29%")&&!SceneText.HasGameHud("点击进入")));
        Check("game HUD accepts OCR spacing and common glyph errors",()=>Expect(SceneText.HasGameHud("U I D 000000000")&&SceneText.HasGameHud("U1D:000000000")&&SceneText.HasGameHud("引 D 000000000")));
        Check("limited banner confirmed",()=>Expect(ResourceReader.LimitedBanner("角 色 活 动 祈 愿")));
        Check("standard banner excluded",()=>Expect(!ResourceReader.LimitedBanner("常驻祈愿 奔行世间")));
        using var header = new Bitmap(1000,140); using var hg = Graphics.FromImage(header); hg.Clear(Color.DarkSlateBlue);
        hg.FillEllipse(Brushes.Violet, 735, 38, 49, 48);
        var digits = new OcrResult("124 0",[new("124",200,50,70,30),new("0",800,50,25,30)]);
        Check("resource digits and pink fate icon",()=> { var r=ResourceReader.ParseHeader(digits,header,true); Expect(r.Valid&&r.Primogems==124&&r.Intertwined==0); });
        Check("round plus OCR zero discarded",()=> { var r=ResourceReader.ParseHeader(digits with {Words=[digits.Words[0],new("0",600,40,48,48),digits.Words[1]]},header,true); Expect(r.Valid&&r.Intertwined==0); });
        Check("extra similar-sized digit rejected",()=>Expect(!ResourceReader.ParseHeader(digits with{Words=[digits.Words[0],new("9",600,50,24,30),digits.Words[1]]},header,true).Valid));
        Check("nonlimited balance rejected",()=>Expect(!ResourceReader.ParseHeader(digits,header,false).Valid));
        using var blue = new Bitmap(1000,140); using(var g=Graphics.FromImage(blue)) g.Clear(Color.CornflowerBlue);
        Check("standard banner blue acquaint icon not counted",()=>Expect(!ResourceReader.ParseHeader(digits,blue,false).Valid));
        Check("confirmed limited label tolerates display color shift",()=>Expect(ResourceReader.ParseHeader(digits,blue,true).Valid));
        Check("numbers outside resource positions rejected",()=>Expect(!ResourceReader.ParseHeader(digits with {Words=[digits.Words[0] with {X=20},digits.Words[1]]},header,true).Valid));
        var consensus = new ResourceConsensus(); var reading = new ResourceDetection(96000,0,"ok");
        Check("one reading cannot update resources",()=>Expect(consensus.Observe(reading,1)==null));
        Check("two consistent readings update",()=>Expect(consensus.Observe(reading,2)?.Pulls==600));
        Check("different reading resets consensus",()=>Expect(consensus.Observe(new(95999,0,"ok"),3)==null));
        Check("unreadable reading resets consensus",()=> { consensus.Observe(new(null,null,"unknown"),4); Expect(consensus.Observe(reading,5)==null); });
        Check("uniform light scene readable",()=> { using var b=new Bitmap(100,100); using var g=Graphics.FromImage(b); g.Clear(Color.LightGray); Expect(Native.HasContent(b)); });
        Check("black capture cannot release",()=> { using var b=new Bitmap(100,100); Expect(!Native.HasContent(b)); });
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey=Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()); long now=1900000000;
        var state=new GuardState { Committed=true };
        var grant=new Grant("wishguard-v2",state.InstallId,state.PolicyId,state.Nonce,"test-pass",now,now+86400,600,"permanent");
        string Sign(Grant g) { var b=JsonSerializer.SerializeToUtf8Bytes(g); return JsonSerializer.Serialize(new Envelope(Rules.Encode(b),Rules.Encode(key.SignData(b,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence)))); }
        string token=Sign(grant);
        Check("valid permanent grant",()=>Expect(Rules.Validate(token,publicKey,state,now,true).VerifiedPulls==600));
        Check("599 pulls not enough",()=>Reject(()=>Rules.Validate(Sign(grant with{VerifiedPulls=599}),publicKey,state,now,true)));
        Check("old policy version rejected",()=>Reject(()=>Rules.Validate(Sign(grant with{Version="wishguard-v1"}),publicKey,state,now,true)));
        Check("temporary release rejected",()=>Reject(()=>Rules.Validate(Sign(grant with{UnlockMode="temporary"}),publicKey,state,now,true)));
        Check("wrong device rejected",()=>Reject(()=>Rules.Validate(Sign(grant with{InstallId="another"}),publicKey,state,now,true)));
        Check("wrong policy rejected",()=>Reject(()=>Rules.Validate(Sign(grant with{PolicyId="another"}),publicKey,state,now,true)));
        Check("old request rejected",()=>Reject(()=>Rules.Validate(Sign(grant with{Nonce="another"}),publicKey,state,now,true)));
        Check("future grant rejected",()=>Reject(()=>Rules.Validate(Sign(grant with{IssuedAt=now+200}),publicKey,state,now,true)));
        Check("expired redemption rejected",()=>Reject(()=>Rules.Validate(token,publicKey,state,now+86400,true)));
        Check("too long redemption rejected",()=>Reject(()=>Rules.Validate(Sign(grant with{RedeemBefore=now+8*86400}),publicKey,state,now,true)));
        Check("tampered payload rejected",()=> { var e=JsonSerializer.Deserialize<Envelope>(token)!; Reject(()=>Rules.Validate(JsonSerializer.Serialize(e with{Payload=Rules.Encode(JsonSerializer.SerializeToUtf8Bytes(grant with{VerifiedPulls=999}))}),publicKey,state,now,true)); });
        state.PermanentPass=token; state.UsedPasses.Add(grant.Id);
        Check("permanent grant has no unlock expiration",()=>Expect(Rules.Validate(token,publicKey,state,now+100*86400,false).UnlockMode=="permanent"&&Rules.Permanent(state,publicKey)));
        Check("used grant cannot be reimported",()=>Reject(()=>Rules.Validate(token,publicKey,state,now,true)));
        Check("permanent completion suppresses startup",()=>Expect(Rules.Ended(state,publicKey)));
        state.LastSeen=now+1000;
        Check("clock rollback detected",()=>Expect(Rules.ClockRolledBack(state,now)));
        var cool=new GuardState {Committed=true}; Rules.RequestRecovery(cool,now);
        Check("24 hour delay",()=>Expect(!Rules.CanFinishRecovery(cool,now+86399)&&Rules.CanFinishRecovery(cool,now+86400)));
        Check("repeated request cannot extend or reset",()=> { Rules.RequestRecovery(cool,now+100); Expect(cool.RecoveryAt==now+86400); });
        Check("cancel removes request",()=> { Rules.CancelRecovery(cool); Expect(cool.RecoveryAt==null&&cool.RecoveryRequestedAt==null&&!Rules.CanFinishRecovery(cool,now+86400)); });
        Check("request after cancellation starts full day",()=> { Rules.RequestRecovery(cool,now+500); Expect(cool.RecoveryAt==now+500+86400); });
        Check("state and resource persistence",()=> { var s=new StateStore(Path.Combine(data,"persist"));s.State.Committed=true;s.State.Resources=new(124,0,now);s.State.RecoveryAt=now+86400;s.Save();var l=new StateStore(s.DirectoryPath);Expect(l.State.Resources?.Primogems==124&&l.State.Resources.PullSOrZero()==0&&l.State.RecoveryAt==now+86400); });
        Check("cancellation persists across restart",()=> { var s=new StateStore(Path.Combine(data,"cancel"));s.State.Committed=true;Rules.RequestRecovery(s.State,now);s.Save();Rules.CancelRecovery(s.State);s.Save();Expect(new StateStore(s.DirectoryPath).State.RecoveryAt==null); });
        Check("permanent completion persists across restart",()=> { var s=new StateStore(Path.Combine(data,"permanent")); s.State.InstallId=state.InstallId;s.State.Nonce=state.Nonce;s.State.Committed=true;s.State.PermanentPass=token;s.Save();Expect(Rules.Ended(new StateStore(s.DirectoryPath).State,publicKey)); });
        Check("v1 migrates to 600 permanent with new nonce",()=> { var d=Path.Combine(data,"migration");Directory.CreateDirectory(d);var old=new GuardState {Schema=1,TargetPulls=300,Committed=true};string nonce=old.Nonce;File.WriteAllText(Path.Combine(d,"state.json"),JsonSerializer.Serialize(old));var s=new StateStore(d);Expect(!s.Fault&&s.State.TargetPulls==600&&s.State.Nonce!=nonce&&File.Exists(Path.Combine(d,"state-v1-backup.json"))); });
        Check("corrupt state does not unlock",()=> {var d=Path.Combine(data,"corrupt");Directory.CreateDirectory(d);File.WriteAllText(Path.Combine(d,"state.json"),"{");var s=new StateStore(d);Expect(s.Fault&&s.State.Committed);});
        Check("missing enrolled state does not unlock",()=> {var d=Path.Combine(data,"missing");Directory.CreateDirectory(d);File.WriteAllText(Path.Combine(d,"initialized"),"1");Expect(new StateStore(d).Fault);});
        // Independent verification key/profile for lifecycle tests; never the real issuer key.
        File.WriteAllText(Path.Combine(data,"test-public.txt"),publicKey);
        var lifecycle=new StateStore(Path.Combine(data,"lifecycle")); lifecycle.State.Committed=true;lifecycle.Save();
        var lg=new Grant("wishguard-v2",lifecycle.State.InstallId,lifecycle.State.PolicyId,lifecycle.State.Nonce,"lifecycle",now,now+86400,600,"permanent");
        File.WriteAllText(Path.Combine(data,"lifecycle-permanent.wishpass"),Sign(lg));
        if (args.Contains("--skip-ocr")) results.Add(new { name = "Windows OCR integration", skipped = true, reason = "Explicit --skip-ocr; CI does not verify OCR language availability." });
        else try
        {
            using var worker=new OcrWorker();await worker.Start();
            using var image=new Bitmap(1100,220);using(var g=Graphics.FromImage(image)){g.Clear(Color.Beige);using var f=new Font("Microsoft YaHei UI",25);g.DrawString("祈愿1次    祈愿10次",f,Brushes.Black,50,100);}
            string text=await worker.Read(image);Check("live Windows OCR wish text",()=>Expect(WishText.IsWish(text)));
            if(Program.GetArg(args,"--fixture") is { } file)
            {
                using var frame=new Bitmap(file);var r=await ResourceReader.Read(frame,worker);
                Check("provided screenshot matches expected resource amounts",()=>Expect(r.Valid&&r.Primogems==long.Parse(Program.GetArg(args,"--expected-primogems")!)&&r.Intertwined==long.Parse(Program.GetArg(args,"--expected-intertwined")!)));
                using var physical = ResourceReader.Crop(frame, new Rectangle(Point.Empty, frame.Size), 1920d / frame.Width);
                var scaled = await ResourceReader.Read(physical, worker);
                Check("provided screenshot resized to 1920 pixels matches expected resource amounts",()=>Expect(scaled.Valid&&scaled.Primogems==r.Primogems&&scaled.Intertwined==r.Intertwined));
                results.Add(new{diagnostic="real screenshot resource result",r.Status,r.Primogems,r.Intertwined});
            }
        }
        catch(Exception e){failed++;results.Add(new{name="Windows OCR integration",pass=false,error=e.ToString()});}
        string report=Program.GetArg(args,"--report")??Path.Combine(data,"results.json");
        File.WriteAllText(report,JsonSerializer.Serialize(new{passed=failed==0,failed,time=DateTimeOffset.Now,results},Json));
        return failed==0?0:1;
    }
    static long PullSOrZero(this ResourceSnapshot? r)=>r?.Pulls??0;
}
