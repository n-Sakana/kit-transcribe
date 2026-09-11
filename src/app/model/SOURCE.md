# Speech model provenance

## Active models

Whisper small and Whisper large-v3-turbo run locally using the existing sherpa-onnx
1.13.4 CPU runtime. The downloadable assets are ONNX int8 conversions, not the
English-only `.en` models and not a cloud API. The upstream `turbo` alias denotes
`large-v3-turbo`.

- Whisper: https://github.com/openai/whisper (MIT)
- ONNX conversion and download documentation: https://k2-fsa.github.io/sherpa/onnx/pretrained_models/whisper/export-onnx.html
- small: https://huggingface.co/csukuangfj/sherpa-onnx-whisper-small/tree/8f3c18b358db4d1f2fc1eae49d75cd20989e4309
- turbo: https://huggingface.co/csukuangfj/sherpa-onnx-whisper-turbo/tree/2ca6ff69fc878651b770880507669577ac41c2ff

`../setup-models.ps1` downloads only these fixed revisions. The encoder/decoder
hashes below are the SHA-256 values of the original assets (not their Xet hashes).
Tokens are pinned to the same revisions and validated as 50,257 sequential
entries: base64-encoded tokens plus the final empty-token marker `= 50256`.
Models are verified again before native initialization.

| Asset | SHA-256 |
|---|---|
| small-encoder.int8.onnx | 4cbe7b22fa9026b843b60a68640c747de05bafb1a11b57edc0e66c232d9f33a9 |
| small-decoder.int8.onnx | acad50b5c782696e91b55914cc5ab4f756f1532f76e22aa6fc615f39fb69a8ee |
| turbo-encoder.int8.onnx | b02dcdf54f348741e93fe732b67d933c8dcb6735655f710640143081db38878b |
| turbo-decoder.int8.onnx | 20accd02388482eb3a46bd615631adfdc85e1eb2c7db9ea3f02a40ffe6b81547 |
| silero_vad.onnx (bundled, MIT) | 9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6 |

Silero VAD is the existing vendored asset from the sherpa-onnx asr-models release:
https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models

## Legacy assets

The existing ReazonSpeech Japanese Zipformer (`reazonspeech-k2-v2`, 2024-08-01
sherpa conversion, Apache-2.0) files remain in the repository for provenance and
rollback. They are no longer loaded or reconstructed. Their original hashes and
licenses remain in `../THIRD-PARTY-NOTICES.md` and `../licenses/`.
