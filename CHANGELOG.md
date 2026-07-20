# Changelog

## [3.1.3](https://github.com/nzbdav/UsenetSharp/compare/v3.1.2...v3.1.3) (2026-07-20)


### Bug Fixes

* **deps:** bump NzbDav.RapidYencSharp to 3.0.0 ([#70](https://github.com/nzbdav/UsenetSharp/issues/70)) ([fdf4757](https://github.com/nzbdav/UsenetSharp/commit/fdf4757ec08dbadf3b6b729ac87f1b25fede86af))

## [3.1.2](https://github.com/nzbdav/UsenetSharp/compare/v3.1.1...v3.1.2) (2026-07-20)


### Bug Fixes

* **deps:** consume RapidYencSharp musl natives for Alpine ([#67](https://github.com/nzbdav/UsenetSharp/issues/67)) ([4af5fa3](https://github.com/nzbdav/UsenetSharp/commit/4af5fa33ab61b1536b07ea8c81c23b7cb8afee66))

## [3.1.1](https://github.com/nzbdav/UsenetSharp/compare/v3.1.0...v3.1.1) (2026-07-19)


### Bug Fixes

* **client:** prevent CoalescedReadTimeout timer callback use-after-dispose under AUTH storms ([#64](https://github.com/nzbdav/UsenetSharp/issues/64)) ([b48aabf](https://github.com/nzbdav/UsenetSharp/commit/b48aabfc6e8adedcaa1a373fb30aeef7ebc8dbef))

## [3.1.0](https://github.com/nzbdav/UsenetSharp/compare/v3.0.0...v3.1.0) (2026-07-19)


### Features

* **client:** add pipelined STAT existence checks (StatPipelinedAsync) ([e9c27fb](https://github.com/nzbdav/UsenetSharp/commit/e9c27fb00f65c6c66f0e70b6c8d4c5543bca83e3))

## [3.0.0](https://github.com/nzbdav/UsenetSharp/compare/v2.0.2...v3.0.0) (2026-07-16)


### ⚠ BREAKING CHANGES

* **nntp:** SegmentIds longer than 248 characters (excluding angle brackets) are now rejected before any command is sent.

### Features

* **client:** add CancellationPolicy for seek-friendly abandon ([acc3963](https://github.com/nzbdav/UsenetSharp/commit/acc3963812e8d3f7162dbad26c7f2127db2dc8be)), closes [#52](https://github.com/nzbdav/UsenetSharp/issues/52)
* **client:** add yEnc header probe API (YencHeadersAsync) ([add7d11](https://github.com/nzbdav/UsenetSharp/commit/add7d1110a6b0cc0fa9221a84ea1af069493ad6f)), closes [#60](https://github.com/nzbdav/UsenetSharp/issues/60)
* **client:** tune TCP keepalive for pooled idle connections ([f73f7fb](https://github.com/nzbdav/UsenetSharp/commit/f73f7fb465317597c711ac0bf36ddd9074afe636)), closes [#51](https://github.com/nzbdav/UsenetSharp/issues/51)


### Bug Fixes

* **client:** adapt cancel-drain timeout test for batched flushes ([5cfcd02](https://github.com/nzbdav/UsenetSharp/commit/5cfcd022e158747f54bc933ff4a67e57f1ccc970))
* **client:** batch raw body pipe flushes ([bc0f6e2](https://github.com/nzbdav/UsenetSharp/commit/bc0f6e2900e7a36e3f6252401268655c3f2f3cd5)), closes [#53](https://github.com/nzbdav/UsenetSharp/issues/53)
* **client:** coalesce per-command I/O timeouts ([b0a45c0](https://github.com/nzbdav/UsenetSharp/commit/b0a45c09ff4122bb780ebac351b9cb996db92af2)), closes [#54](https://github.com/nzbdav/UsenetSharp/issues/54)
* **client:** read connection health without state lock ([aacf1ab](https://github.com/nzbdav/UsenetSharp/commit/aacf1abe204bb2a14bf4ebd2d9216602bc6f9b9a)), closes [#59](https://github.com/nzbdav/UsenetSharp/issues/59)
* **client:** remove command setup closure and LINQ allocations ([db47bf8](https://github.com/nzbdav/UsenetSharp/commit/db47bf876c0295b2a1ca104b4626e91cba43fd3b)), closes [#58](https://github.com/nzbdav/UsenetSharp/issues/58)
* **client:** reuse decode buffer and timeout across batch bodies ([b221719](https://github.com/nzbdav/UsenetSharp/commit/b2217197cc8b57ef55ba52cfc90ee95b9ff2b9eb)), closes [#55](https://github.com/nzbdav/UsenetSharp/issues/55)
* **client:** share PipeOptions and align segment size ([938e93f](https://github.com/nzbdav/UsenetSharp/commit/938e93f6598fbf1bea9901bd3aeedffd78dade9a)), closes [#56](https://github.com/nzbdav/UsenetSharp/issues/56)
* **client:** write AUTHINFO without retained StreamWriter ([8f52987](https://github.com/nzbdav/UsenetSharp/commit/8f52987569f04b2645b150595fd73e781b113829)), closes [#57](https://github.com/nzbdav/UsenetSharp/issues/57)
* **deps:** Bump the github-actions group with 3 updates ([#50](https://github.com/nzbdav/UsenetSharp/issues/50)) ([5c35f05](https://github.com/nzbdav/UsenetSharp/commit/5c35f0502fbabcefeac0ae6cacc56ffb02bf4a33))
* **nntp:** address protocol compliance audit findings ([#43](https://github.com/nzbdav/UsenetSharp/issues/43)) ([9c642d3](https://github.com/nzbdav/UsenetSharp/commit/9c642d39ba9fb2b492ced1c4b1a7eca3d38ded89))
* **nntp:** address second-pass audit residuals (S2-01…S2-04) ([#49](https://github.com/nzbdav/UsenetSharp/issues/49)) ([895a693](https://github.com/nzbdav/UsenetSharp/commit/895a6937e1a8324493a780b2a69e356fd6c7eadf)), closes [#48](https://github.com/nzbdav/UsenetSharp/issues/48)


### Performance Improvements

* implement third-pass audit findings (P-01…P-10) ([8c49a05](https://github.com/nzbdav/UsenetSharp/commit/8c49a05a917bb0214a0cdabddb0261fc8e7712da))

## [2.0.2](https://github.com/nzbdav/UsenetSharp/compare/v2.0.1...v2.0.2) (2026-07-11)


### Bug Fixes

* **nntp:** preserve reader state across cancelled refills ([2f40476](https://github.com/nzbdav/UsenetSharp/commit/2f40476bd977c0592cb3586eca7122db753724d8))

## [2.0.1](https://github.com/nzbdav/UsenetSharp/compare/v2.0.0...v2.0.1) (2026-07-11)


### Bug Fixes

* **deps:** consume NzbDav.RapidYencSharp 2.0.2 ([b211a31](https://github.com/nzbdav/UsenetSharp/commit/b211a31cf7174e4fe984620640e88c590363403e))
* **deps:** consume RapidYencSharp 2.0.0 ([1fa6e8a](https://github.com/nzbdav/UsenetSharp/commit/1fa6e8a0a5d38bc1200f828a4de81c487f67b069))

## [2.0.0](https://github.com/nzbdav/UsenetSharp/compare/v1.2.4...v2.0.0) (2026-07-11)


### ⚠ BREAKING CHANGES

* **runtime:** UsenetSharp now targets net10.0 only. .NET 9 is no longer supported.

### Features

* **runtime:** target .NET 10 and optimize hot paths ([72c0300](https://github.com/nzbdav/UsenetSharp/commit/72c0300f896ed1c14408884ea0acabe2d567b020))

## [1.2.4](https://github.com/nzbdav/UsenetSharp/compare/v1.2.3...v1.2.4) (2026-07-11)


### Bug Fixes

* **client:** prevent stale cancellation propagation ([ad63088](https://github.com/nzbdav/UsenetSharp/commit/ad630880d7c1a380094de049d3ddb0fd09737f6d))

## [1.2.3](https://github.com/nzbdav/UsenetSharp/compare/v1.2.2...v1.2.3) (2026-07-10)


### Bug Fixes

* **release:** publish package through NuGet.org ([e7ba60e](https://github.com/nzbdav/UsenetSharp/commit/e7ba60e2eadb3df33e48e1fc5811a75a5c2f2aea))

## [1.2.2](https://github.com/nzbdav/UsenetSharp/compare/v1.2.1...v1.2.2) (2026-07-10)


### Bug Fixes

* **nntp:** accept derived cancellation exceptions ([303ea28](https://github.com/nzbdav/UsenetSharp/commit/303ea282af8a6db15419816fa567456e1f915645))

## [1.2.1](https://github.com/nzbdav/UsenetSharp/compare/v1.2.0...v1.2.1) (2026-07-10)


### Bug Fixes

* **nntp:** preserve connection health on cancellation ([9d74828](https://github.com/nzbdav/UsenetSharp/commit/9d74828f06e4b357069dd4ed6b2263450fce1ba7))

## [1.2.0](https://github.com/nzbdav/UsenetSharp/compare/v1.1.0...v1.2.0) (2026-07-10)


### Features

* **nntp:** make certificate revocation checks configurable ([60cc2c5](https://github.com/nzbdav/UsenetSharp/commit/60cc2c5d03e6bd58ea85305fd641162c44e3f4fe))
* **nntp:** pipeline decoded BODY commands ([144c8a1](https://github.com/nzbdav/UsenetSharp/commit/144c8a1ac08281265c86d8efb9817022bee38a74))

## [1.1.0](https://github.com/nzbdav/UsenetSharp/compare/v1.0.8...v1.1.0) (2026-07-10)


### Features

* **yenc:** add optional decoded body CRC32 validation ([3a4d1ce](https://github.com/nzbdav/UsenetSharp/commit/3a4d1ce5a5cefba9dd539dddf10f8fcefdb26520))
* **yenc:** decode article bodies in raw chunks ([0f90585](https://github.com/nzbdav/UsenetSharp/commit/0f90585872c7b585368e3a19814eabcbf7c8051c))


### Bug Fixes

* **nntp:** coalesce article body read timeouts ([0f456c3](https://github.com/nzbdav/UsenetSharp/commit/0f456c3c2f6fc5d46faec9654e045271f311d50e))
* **nntp:** read article data in 64 KiB chunks ([3320dc3](https://github.com/nzbdav/UsenetSharp/commit/3320dc3df5b3cfd0e693bafc18634d51464c7535))

## [1.0.8](https://github.com/nzbdav/UsenetSharp/compare/v1.0.7...v1.0.8) (2026-07-10)


### Bug Fixes

* **ci:** publish packages after release creation ([79682b1](https://github.com/nzbdav/UsenetSharp/commit/79682b10a7a9644a6c2859b61b77071a56985e70))
* **client:** enable NoDelay and TCP keepalive on connect ([4e29af2](https://github.com/nzbdav/UsenetSharp/commit/4e29af250ffa64365bea877558a458843ee1b746))
* **client:** expose IsConnected and IsHealthy for pool consumers ([27b0e27](https://github.com/nzbdav/UsenetSharp/commit/27b0e27debc00706d95af5ea941b0b26ebc0d265))
* **client:** guard connection token swap against disposal race ([36e52f4](https://github.com/nzbdav/UsenetSharp/commit/36e52f4ce3bc9403516658c2aa733c809c284ef1))
* **client:** make concurrent disposal single-shot ([741978f](https://github.com/nzbdav/UsenetSharp/commit/741978fa830300750aed4c6b1dd342bf1b40cc1b))
* **client:** make read timeout and drain limit configurable ([be29745](https://github.com/nzbdav/UsenetSharp/commit/be29745930e3fd90e62da5a7116389e6efbc2119))
* **client:** use private state lock and fault-tolerant connection cleanup ([998b215](https://github.com/nzbdav/UsenetSharp/commit/998b215afe9ecaad1b322be74a17d2776cecf2ea))
* **nntp:** bound drain of abandoned bodies and survive cancellation ([395cd8c](https://github.com/nzbdav/UsenetSharp/commit/395cd8c202bd2033ed5bed5bde431b9d8ca648eb))
* **nntp:** reuse a re-armed timeout token across body reads ([1394b2f](https://github.com/nzbdav/UsenetSharp/commit/1394b2fc1c2e3a0e26afcb15ffd4e5652faf0cca))
* **nntp:** stream body bytes without per-line string allocation ([af38400](https://github.com/nzbdav/UsenetSharp/commit/af384009f7843297584190d601db931b3f5e5b77))
* prevent disposal race from skipping onConnectionReadyAgain ([61a5cd5](https://github.com/nzbdav/UsenetSharp/commit/61a5cd5cc6fac755aae3484843e34f029c9f3915))
* **yenc:** distinguish empty lines from end of stream ([f2ef0f5](https://github.com/nzbdav/UsenetSharp/commit/f2ef0f5b4abc724a0f4b8afc4f152febddd8a955))

## [1.0.7](https://github.com/nzbdav/UsenetSharp/compare/v1.0.6...v1.0.7) (2026-07-10)


### Bug Fixes

* **deps:** bump actions/checkout from 4 to 7 ([#3](https://github.com/nzbdav/UsenetSharp/issues/3)) ([b6b0800](https://github.com/nzbdav/UsenetSharp/commit/b6b08002ad382fec1e0dae200e7bd87be38d53d0))
* **deps:** bump actions/setup-dotnet from 4 to 5 ([#2](https://github.com/nzbdav/UsenetSharp/issues/2)) ([42bbd58](https://github.com/nzbdav/UsenetSharp/commit/42bbd589934a35d947e7d88359bab756614b5fea))
* **deps:** bump googleapis/release-please-action from 4 to 5 ([#1](https://github.com/nzbdav/UsenetSharp/issues/1)) ([ebc591c](https://github.com/nzbdav/UsenetSharp/commit/ebc591ccc9ded70042ebf17f2f6ff7a71d35297a))

## Changelog

Release Please maintains this file from Conventional Commit history. Historical
notes for releases through 1.0.6 were not available when automated release
management was introduced.
