# Changelog

## [1.0.8](https://github.com/hoivikaj/UsenetSharp/compare/v1.0.7...v1.0.8) (2026-07-10)


### Bug Fixes

* **ci:** publish packages after release creation ([79682b1](https://github.com/hoivikaj/UsenetSharp/commit/79682b10a7a9644a6c2859b61b77071a56985e70))
* **client:** enable NoDelay and TCP keepalive on connect ([4e29af2](https://github.com/hoivikaj/UsenetSharp/commit/4e29af250ffa64365bea877558a458843ee1b746))
* **client:** expose IsConnected and IsHealthy for pool consumers ([27b0e27](https://github.com/hoivikaj/UsenetSharp/commit/27b0e27debc00706d95af5ea941b0b26ebc0d265))
* **client:** guard connection token swap against disposal race ([36e52f4](https://github.com/hoivikaj/UsenetSharp/commit/36e52f4ce3bc9403516658c2aa733c809c284ef1))
* **client:** make concurrent disposal single-shot ([741978f](https://github.com/hoivikaj/UsenetSharp/commit/741978fa830300750aed4c6b1dd342bf1b40cc1b))
* **client:** make read timeout and drain limit configurable ([be29745](https://github.com/hoivikaj/UsenetSharp/commit/be29745930e3fd90e62da5a7116389e6efbc2119))
* **client:** use private state lock and fault-tolerant connection cleanup ([998b215](https://github.com/hoivikaj/UsenetSharp/commit/998b215afe9ecaad1b322be74a17d2776cecf2ea))
* **nntp:** bound drain of abandoned bodies and survive cancellation ([395cd8c](https://github.com/hoivikaj/UsenetSharp/commit/395cd8c202bd2033ed5bed5bde431b9d8ca648eb))
* **nntp:** reuse a re-armed timeout token across body reads ([1394b2f](https://github.com/hoivikaj/UsenetSharp/commit/1394b2fc1c2e3a0e26afcb15ffd4e5652faf0cca))
* **nntp:** stream body bytes without per-line string allocation ([af38400](https://github.com/hoivikaj/UsenetSharp/commit/af384009f7843297584190d601db931b3f5e5b77))
* prevent disposal race from skipping onConnectionReadyAgain ([61a5cd5](https://github.com/hoivikaj/UsenetSharp/commit/61a5cd5cc6fac755aae3484843e34f029c9f3915))
* **yenc:** distinguish empty lines from end of stream ([f2ef0f5](https://github.com/hoivikaj/UsenetSharp/commit/f2ef0f5b4abc724a0f4b8afc4f152febddd8a955))

## [1.0.7](https://github.com/hoivikaj/UsenetSharp/compare/v1.0.6...v1.0.7) (2026-07-10)


### Bug Fixes

* **deps:** bump actions/checkout from 4 to 7 ([#3](https://github.com/hoivikaj/UsenetSharp/issues/3)) ([b6b0800](https://github.com/hoivikaj/UsenetSharp/commit/b6b08002ad382fec1e0dae200e7bd87be38d53d0))
* **deps:** bump actions/setup-dotnet from 4 to 5 ([#2](https://github.com/hoivikaj/UsenetSharp/issues/2)) ([42bbd58](https://github.com/hoivikaj/UsenetSharp/commit/42bbd589934a35d947e7d88359bab756614b5fea))
* **deps:** bump googleapis/release-please-action from 4 to 5 ([#1](https://github.com/hoivikaj/UsenetSharp/issues/1)) ([ebc591c](https://github.com/hoivikaj/UsenetSharp/commit/ebc591ccc9ded70042ebf17f2f6ff7a71d35297a))

## Changelog

Release Please maintains this file from Conventional Commit history. Historical
notes for releases through 1.0.6 were not available when automated release
management was introduced.
