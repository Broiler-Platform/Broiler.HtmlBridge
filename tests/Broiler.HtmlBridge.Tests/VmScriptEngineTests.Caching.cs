#if BROILER_VM_JS

using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.VM.Profile.JavaScript.Compiler;

namespace Broiler.HtmlBridge.Tests;

public partial class VmScriptEngineTests
{
    /// <summary>
    /// The compilation cache: what it must save, and what it must never get wrong.
    /// </summary>
    /// <remarks>
    /// <b>Every case here counts compilations rather than timing them.</b> "The second run compiled
    /// nothing" is a fact a machine either reproduces or does not; "the second run was faster" is a
    /// measurement, and a measurement inside a test is a flake waiting for a loaded build agent.
    /// Each test uses its own cache instance so the shared one cannot make one test's result depend
    /// on another's having run first.
    /// </remarks>
    public class Caching
    {
        private static VmScriptEngine Engine(VmCompilationCache cache) =>
            new(new RecordingEngine()) { Cache = cache };

        private static VmCompilationCache Fresh() => new(maximumEntries: 8, maximumBytes: 1 << 20);

        [Fact]
        public void TheSameDocumentIsCompiledOnce()
        {
            var cache = Fresh();
            string[] scripts = ["var a = 1;", "var b = a + 1;"];

            Assert.True(Engine(cache).Execute(scripts));
            Assert.Equal(1, cache.Compilations);

            Assert.True(Engine(cache).Execute(scripts));
            Assert.Equal(1, cache.Compilations);
            Assert.Equal(1, cache.Hits);
        }

        [Fact]
        public void ADifferentDocumentIsCompiledAgain()
        {
            var cache = Fresh();

            Engine(cache).Execute(["var a = 1;"]);
            Engine(cache).Execute(["var a = 2;"]);

            Assert.Equal(2, cache.Compilations);
            Assert.Equal(0, cache.Hits);
        }

        /// <summary>
        /// Strict mode changes what the compiler emits, so it has to change the key. Serving the
        /// sloppy artifact to a strict document would run a different program than the one asked
        /// for — the failure a cache key exists to prevent.
        /// </summary>
        [Fact]
        public void StrictModeIsPartOfTheIdentity()
        {
            var cache = Fresh();
            string[] scripts = ["var a = 1;"];

            Engine(cache).Execute(scripts);

            var strict = Engine(cache);
            strict.StrictModeEnabled = true;
            strict.Execute(scripts);

            Assert.Equal(2, cache.Compilations);
            Assert.Equal(0, cache.Hits);
        }

        /// <summary>
        /// Two documents whose script lists are byte-identical SHARE an entry when no document URL
        /// is supplied — which is correct, and is what most callers get.
        /// </summary>
        /// <remarks>
        /// <c>Execute(scripts)</c> and <c>ExecuteDetailed(scripts)</c> pass no URL, so the referrer
        /// is empty and the identity is the script list and the compilation flags. Two pages with
        /// the same scripts then compile to the same program, byte for byte, and sharing is not a
        /// collision but the point. It is pinned because the opposite was asserted in this branch's
        /// first description of the cache, and prose about a key is worth exactly what a test says
        /// it is.
        /// </remarks>
        [Fact]
        public void WithoutADocumentUrlIdenticalScriptsShare()
        {
            var cache = Fresh();
            string[] scripts = ["var shared = 1;"];

            Engine(cache).Execute(scripts);
            Engine(cache).Execute(scripts);

            Assert.Equal(1, cache.Compilations);
            Assert.Equal(1, cache.Hits);
        }

        /// <summary>
        /// And with one supplied it separates them, because the URL is what a relative specifier
        /// resolves against — so the same text under two documents is two programs.
        /// </summary>
        [Fact]
        public void TheDocumentUrlIsPartOfTheIdentity()
        {
            var cache = Fresh();
            string[] scripts = ["var a = 1;"];

            Engine(cache).ExecuteDetailed(scripts, null, "https://example.test/one");
            Engine(cache).ExecuteDetailed(scripts, null, "https://example.test/two");

            Assert.Equal(2, cache.Compilations);
            Assert.Equal(0, cache.Hits);
        }

        /// <summary>A cached document still runs, and still produces its value.</summary>
        /// <remarks>
        /// The counter says compilation was skipped; this says the bytes that were served are the
        /// program. A cache that returned stale or truncated bytes would satisfy the counter and
        /// fail here.
        /// </remarks>
        [Fact]
        public void ACachedDocumentStillRuns()
        {
            var cache = Fresh();
            string[] scripts = ["print('cached=' + (20 + 22));"];

            Assert.Contains("cached=42", Printed(cache, scripts));
            Assert.Contains("cached=42", Printed(cache, scripts));
            Assert.Equal(1, cache.Compilations);
        }

        /// <summary>
        /// Two scripts differing only in a lone surrogate are two entries.
        /// </summary>
        /// <remarks>
        /// <b>This is the case that made the key hash UTF-16 code units instead of UTF-8.</b>
        /// <c>Encoding.UTF8.GetBytes</c> uses replacement fallback, so <c>\uD800</c> and
        /// <c>\uD801</c> both encode to the replacement bytes and hashed identically — and this is
        /// reachable rather than theoretical, because the profile's tokenizer accepts a lone
        /// surrogate as a legal JavaScript string element and says so. The cache would have served
        /// one page's program to another.
        /// </remarks>
        [Fact]
        public void ALoneSurrogateIsPartOfTheIdentity()
        {
            var cache = Fresh();

            Engine(cache).Execute(["var s = '\uD800';"]);
            Engine(cache).Execute(["var s = '\uD801';"]);

            Assert.Equal(2, cache.Compilations);
            Assert.Equal(0, cache.Hits);
        }

        /// <summary>A refusal is not stored, so a broken script is not cached as broken.</summary>
        [Fact]
        public void ARefusalIsNotCached()
        {
            var cache = Fresh();
            string[] scripts = ["var broken = ;"];

            Assert.False(Engine(cache).Execute(scripts));
            Assert.False(Engine(cache).Execute(scripts));

            Assert.Equal(0, cache.Hits);
        }

        /// <summary>The bound is enforced, and the least recently used entry is the one that goes.</summary>
        [Fact]
        public void TheCacheIsBounded()
        {
            var cache = new VmCompilationCache(maximumEntries: 2, maximumBytes: 1 << 20);

            Engine(cache).Execute(["var a = 1;"]);
            Engine(cache).Execute(["var b = 2;"]);
            Engine(cache).Execute(["var a = 1;"]);   // keeps the first entry the most recent
            Engine(cache).Execute(["var c = 3;"]);   // evicts `b`, the least recently used

            Assert.Equal(3, cache.Compilations);
            Assert.Equal(1, cache.Hits);

            Engine(cache).Execute(["var a = 1;"]);   // still resident
            Assert.Equal(2, cache.Hits);

            Engine(cache).Execute(["var b = 2;"]);   // was evicted, so compiles again
            Assert.Equal(4, cache.Compilations);
        }

        /// <summary>
        /// One body evaluated repeatedly is compiled once — the pattern that actually repeats.
        /// </summary>
        /// <remarks>
        /// <c>new Function</c> with one body called from a loop, or a template evaluated per row.
        /// The guest asks the host to compile every time; the host answers from what it compiled
        /// the first time.
        /// </remarks>
        [Fact]
        public void ARepeatedEvalIsCompiledOnce()
        {
            var engine = Engine(Fresh());
            var guest = Fresh();
            engine.GuestLoadCache = guest;

            Assert.True(engine.Execute(["for (var i = 0; i < 5; i++) { (0, eval)('1 + 1'); }"]));

            Assert.Equal(1, guest.Compilations);
            Assert.Equal(4, guest.Hits);
        }

        [Fact]
        public void DistinctEvalsAreCompiledSeparately()
        {
            var engine = Engine(Fresh());
            var guest = Fresh();
            engine.GuestLoadCache = guest;

            Assert.True(engine.Execute(["(0, eval)('1 + 1'); (0, eval)('2 + 2');"]));

            Assert.Equal(2, guest.Compilations);
            Assert.Equal(0, guest.Hits);
        }

        /// <summary>A cached eval still answers, and still answers the right value.</summary>
        [Fact]
        public void ARepeatedEvalStillProducesItsValue()
        {
            var engine = Engine(Fresh());
            var guest = Fresh();
            engine.GuestLoadCache = guest;

            var written = new List<string>();
            void Capture(RenderLogEntry entry) => written.Add(entry.Message);

            RenderLogger.EntryLogged += Capture;
            try
            {
                engine.Execute(["print('a=' + (0, eval)('20 + 22')); print('b=' + (0, eval)('20 + 22'));"]);
            }
            finally
            {
                RenderLogger.EntryLogged -= Capture;
            }

            var all = string.Join("\n", written);
            Assert.Contains("a=42", all);
            Assert.Contains("b=42", all);
            Assert.Equal(1, guest.Compilations);
        }

        /// <summary>
        /// The two caches are separate, and a page's evaluation cannot reach the shared one.
        /// </summary>
        /// <remarks>
        /// This is the eviction half of why guest source is scoped per engine: were it one cache, a
        /// page that evaluated enough distinct strings would push out every other page's compiled
        /// document. The timing half cannot be asserted here — it is an argument about what a page
        /// could learn, not about a counter — and is recorded in <c>GuestLoadCache</c>'s remarks.
        /// </remarks>
        [Fact]
        public void GuestSourceDoesNotEnterTheDocumentCache()
        {
            var document = Fresh();
            var guest = Fresh();
            var engine = Engine(document);
            engine.GuestLoadCache = guest;

            engine.Execute(["(0, eval)('1 + 1');"]);

            Assert.Equal(1, document.Compilations);   // the document itself, and nothing more
            Assert.Equal(1, guest.Compilations);      // the eval, in its own cache
        }

        /// <summary>
        /// The on-disk tier: what it survives, and what it must refuse to serve.
        /// </summary>
        /// <remarks>
        /// Each case gets its own directory under the test run's temporary path, so nothing here
        /// touches the real cache location and two cases cannot see each other.
        /// </remarks>
        public sealed class OnDisk : IDisposable
        {
            private readonly string _directory =
                Path.Combine(Path.GetTempPath(), "broiler-vm-cache-test", Guid.NewGuid().ToString("N"));

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(_directory))
                        Directory.Delete(_directory, recursive: true);
                }
                catch (IOException)
                {
                    // A leftover temporary directory is not worth failing a test over.
                }
            }

            private VmCompilationCache Cache() =>
                new(maximumEntries: 8, maximumBytes: 1 << 20) { Store = new VmArtifactStore(_directory, 1 << 20) };

            private static VmScriptEngine Engine(VmCompilationCache cache) =>
                new(new RecordingEngine()) { Cache = cache };

            /// <summary>
            /// A second cache with a cold memory tier and the same directory compiles nothing —
            /// which is the only thing disk buys over memory.
            /// </summary>
            [Fact]
            public void ADocumentCompiledOnceSurvivesAColdMemoryTier()
            {
                string[] scripts = ["var persisted = 1 + 1;"];

                var first = Cache();
                Assert.True(Engine(first).Execute(scripts));
                Assert.Equal(1, first.Compilations);

                // A fresh cache is what the next run of the process has: nothing in memory, the
                // same directory on disk.
                var second = Cache();
                Assert.True(Engine(second).Execute(scripts));

                Assert.Equal(0, second.Compilations);
                Assert.Equal(1, second.Hits);
            }

            /// <summary>And it still runs, rather than merely being found.</summary>
            [Fact]
            public void APersistedDocumentStillProducesItsValue()
            {
                string[] scripts = ["print('disk=' + (20 + 22));"];

                Engine(Cache()).Execute(scripts);

                var written = new List<string>();
                void Capture(RenderLogEntry entry) => written.Add(entry.Message);

                var second = Cache();
                RenderLogger.EntryLogged += Capture;
                try
                {
                    Engine(second).Execute(scripts);
                }
                finally
                {
                    RenderLogger.EntryLogged -= Capture;
                }

                Assert.Contains("disk=42", string.Join("\n", written));
                Assert.Equal(0, second.Compilations);
            }

            /// <summary>
            /// A truncated file is a miss, not a crash and not a refusal the page sees.
            /// </summary>
            [Fact]
            public void ATruncatedFileIsAMiss()
            {
                string[] scripts = ["var truncated = 1;"];
                Engine(Cache()).Execute(scripts);

                var file = Directory.GetFiles(_directory, "*.bin").Single();
                File.WriteAllBytes(file, File.ReadAllBytes(file)[..8]);

                var second = Cache();
                Assert.True(Engine(second).Execute(scripts));
                Assert.Equal(1, second.Compilations);
                Assert.Equal(0, second.Hits);
            }

            /// <summary>Garbage where an artifact should be is a miss too.</summary>
            [Fact]
            public void GarbageIsAMiss()
            {
                string[] scripts = ["var garbage = 1;"];
                Engine(Cache()).Execute(scripts);

                var file = Directory.GetFiles(_directory, "*.bin").Single();
                File.WriteAllBytes(file, Enumerable.Repeat((byte)0xAB, 512).ToArray());

                var second = Cache();
                Assert.True(Engine(second).Execute(scripts));
                Assert.Equal(1, second.Compilations);
            }

            /// <summary>
            /// A file whose embedded key is not the one asked for is refused, even though its bytes
            /// are a perfectly valid artifact.
            /// </summary>
            /// <remarks>
            /// This is the substitution case the digest inside the file exists for: a directory
            /// copied between machines, a partial rename, a filesystem that folded case. It does not
            /// defend against someone who can write both the name and the contents — nothing here
            /// could — and <c>VmArtifactStore</c> says so.
            /// </remarks>
            [Fact]
            public void AFileHoldingAnotherDocumentsArtifactIsRefused()
            {
                Engine(Cache()).Execute(["print('one=1');"]);
                var one = Directory.GetFiles(_directory, "*.bin").Single();
                var onesBytes = File.ReadAllBytes(one);
                File.Delete(one);

                string[] other = ["print('two=2');"];
                Engine(Cache()).Execute(other);
                var two = Directory.GetFiles(_directory, "*.bin").Single();

                // The first document's whole file, under the second document's name.
                File.WriteAllBytes(two, onesBytes);

                var written = new List<string>();
                void Capture(RenderLogEntry entry) => written.Add(entry.Message);

                var third = Cache();
                RenderLogger.EntryLogged += Capture;
                try
                {
                    Engine(third).Execute(other);
                }
                finally
                {
                    RenderLogger.EntryLogged -= Capture;
                }

                var all = string.Join("\n", written);
                Assert.Contains("two=2", all);       // the document that was asked for
                Assert.DoesNotContain("one=1", all); // and not the one whose bytes were planted
                Assert.Equal(1, third.Compilations);
            }

            /// <summary>Clearing removes what was written, so enabling the store is reversible.</summary>
            /// <remarks>
            /// The gate on turning this on is whether a user can undo it, so the undo is tested.
            /// </remarks>
            [Fact]
            public void ClearingRemovesEverythingWritten()
            {
                var store = new VmArtifactStore(_directory, 1 << 20);
                var cache = new VmCompilationCache(8, 1 << 20) { Store = store };

                Engine(cache).Execute(["var cleared = 1;"]);
                Assert.NotEmpty(Directory.GetFiles(_directory, "*.bin"));

                store.Clear();

                Assert.False(Directory.Exists(_directory));

                // And a cold cache over the cleared directory compiles again rather than erroring.
                var second = Cache();
                Assert.True(Engine(second).Execute(["var cleared = 1;"]));
                Assert.Equal(1, second.Compilations);
            }

            /// <summary>
            /// Clearing a cache that was never written is safe — which is the likeliest press of
            /// the button.
            /// </summary>
            /// <remarks>
            /// The store is off by default, so most of the time the directory does not exist. A
            /// user who opens the command and confirms it must get "cleared", not an exception.
            /// </remarks>
            [Fact]
            public void ClearingWhatWasNeverWrittenIsSafe()
            {
                var absent = Path.Combine(_directory, "never-written");

                Assert.False(Directory.Exists(absent));

                new VmArtifactStore(absent, 1 << 20).Clear();   // must not throw

                Assert.False(Directory.Exists(absent));
            }

            /// <summary>
            /// The default location is under local application data, beside the browser's own
            /// folder — pinned because the command deletes whatever is there.
            /// </summary>
            [Fact]
            public void TheDefaultDirectoryIsUnderLocalApplicationData()
            {
                var expected = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Broiler",
                    "vm-code-cache");

                Assert.Equal(expected, VmArtifactStore.DefaultDirectory);
            }

            /// <summary>An unwritable directory degrades to no cache at all, not to an error.</summary>
            [Fact]
            public void AnUnusableDirectoryIsNotAFailure()
            {
                var cache = new VmCompilationCache(8, 1 << 20)
                {
                    // A path under a file rather than a directory: creating it must fail.
                    Store = new VmArtifactStore(Path.Combine(GetType().Assembly.Location, "nope"), 1 << 20),
                };

                Assert.True(Engine(cache).Execute(["var unwritable = 1;"]));
                Assert.Equal(1, cache.Compilations);
            }
        }

        /// <summary>
        /// The shapes the cache key mirrors, pinned so that one growing a field fails HERE.
        /// </summary>
        /// <remarks>
        /// <b>This is the way this cache is most likely to go wrong, and it cannot be caught by
        /// any test of behaviour.</b> The five records are positional, live in the Broiler.VM
        /// submodule, and take their optional parameters with defaults — so a new field added
        /// upstream compiles here unchanged, is read by the compiler, changes the artifact, and is
        /// silently absent from the key. The cache would then serve one program for another with
        /// nothing failing anywhere.
        /// <para>
        /// A gitlink bump that adds a field trips this instead. When it does, the fix is to add the
        /// field to <c>VmCompilationCache.Key</c> and then to this list — in that order.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(typeof(JsScriptUnit), "Name,Options,Referrer,Text,ForceStrict")]
        [InlineData(typeof(JsModuleUnit), "Key,Options,Requests,Text")]
        [InlineData(typeof(JsResolvedRequest), "Key,Specifier")]
        [InlineData(typeof(JsCompileRequest), "Backend,Form,Manifest")]
        [InlineData(typeof(SliceParseOptions), "AllowTopLevelAwait,Goal,GoalIsStrict,MaximumNestingDepth")]
        public void TheKeyMirrorsEveryFieldOfTheCompilerInputs(Type shape, string expected)
        {
            var actual = shape
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(n => n != "EqualityContract")
                .OrderBy(n => n, StringComparer.Ordinal);

            Assert.Equal(
                expected.Split(',').OrderBy(n => n, StringComparer.Ordinal),
                actual);
        }

        private static string Printed(VmCompilationCache cache, IReadOnlyList<string> scripts)
        {
            var written = new List<string>();
            void Capture(RenderLogEntry entry) => written.Add(entry.Message);

            RenderLogger.EntryLogged += Capture;
            try
            {
                Engine(cache).Execute(scripts);
            }
            finally
            {
                RenderLogger.EntryLogged -= Capture;
            }

            return string.Join("\n", written);
        }
    }
}

#endif
