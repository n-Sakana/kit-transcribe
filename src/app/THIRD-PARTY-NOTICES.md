# Third-party components

transcribe runs entirely offline, but it vendors the following components and
model files so that the target PC does not need to install or download them.

| Component | Version/source | License |
|---|---|---|
| sherpa-onnx managed and native libraries | 1.13.4, https://github.com/k2-fsa/sherpa-onnx | Apache-2.0 |
| ONNX Runtime native library | 1.27.0, bundled by sherpa-onnx | MIT |
| NAudio | 1.10.0, https://github.com/naudio/NAudio | MIT |
| ReazonSpeech Japanese Zipformer model | reazonspeech-k2-v2 / 2024-08-01 sherpa conversion | Apache-2.0 |
| Silero VAD model | sherpa-onnx asr-models release asset | MIT |

The corresponding license texts are in `licenses/`. Model provenance is also
recorded in `model/SOURCE.md`.

## Vendored file checksums (SHA-256)

| File | SHA-256 |
|---|---|
| bin/NAudio.dll | BC4BACC3B8B28D898F1671B79F216CCA439F95EB60CD32D3E3ECAFBECAC42780 |
| bin/sherpa-onnx.dll | B6D9A12D659C742A3D9E4D72204186B50AFBD6C56A0F36893F2DC0DCE627245F |
| bin/sherpa-onnx-c-api.dll | 614878147C05121AEB1514EC4FB3E48B89751591532ECA9208235B9AB868306A |
| bin/onnxruntime.dll | DAA77083A45BF525DA0DDE9E87F85D8EB146F58F9C9AA7124CA84545E1C0F148 |
| model/encoder-epoch-99-avg-1.int8.onnx.part01 | 48895C41020DA39B020128252B9053152E0C5BB6CA555C49DFD5756811223517 |
| model/encoder-epoch-99-avg-1.int8.onnx.part02 | 9B06F691E3505A5EE5760E57EB559E40A5C95CCF9FB08881717DD5EB74EC4F87 |
| reconstructed encoder-epoch-99-avg-1.int8.onnx | 2C7BD08A8A99F9DDD0D9E458456577B1F6279214E51426F114F9ECED44C54E1D |
| model/decoder-epoch-99-avg-1.int8.onnx | 8F0BFF94D38797B03B762634ED03211A8E303D06CC4603CDD0CF4199D6EB1485 |
| model/joiner-epoch-99-avg-1.int8.onnx | 49CC7EA1D3D35A40A27442DB5E89996DA64BF0E683A903DCE76E99E57A12E4DE |
| model/tokens.txt | 2C3AC659818A48A0C04010E0593BBC4D7C8A24A054340B01131499C05FD52DEF |
| model/silero_vad.onnx | 9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6 |
