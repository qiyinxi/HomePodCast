"""Reproducibly prune independent Drums/Bass branches; never train or change vocal weights.

Developer dependency: onnx==1.20.1. Input is the pinned, verified StemgenRT 0.7.0 graph.
The module carries the original MIT license. The graph still estimates the previous 128-frame hop.
"""
import hashlib
import sys
from pathlib import Path
import onnx
from onnx import helper, TensorProto, utils

source, target = map(Path, sys.argv[1:3])
assert hashlib.sha256(source.read_bytes()).hexdigest() == "0a49388dd2a47986ab9a161c8764c22853b4d601c188ac052eb530f076d513c7"
model = onnx.load(source)
node = next(n for n in model.graph.node if "separated_chunk" in n.output)
assert node.op_type == "Concat" and node.input[2] == "/vocals/Div_2_output_0"
name = node.input[2]
model.graph.value_info.append(helper.make_tensor_value_info(name, TensorProto.FLOAT, [1, 1, 2, 128]))
inputs = [i.name for i in model.graph.input if i.name == "audio_chunk" or i.name.startswith("vocals_")]
outputs = [name] + [o.name for o in model.graph.output if o.name.startswith("next_vocals_")]
assert len(inputs) == len(outputs) == 5
pruned = utils.Extractor(model).extract_model(inputs, outputs)
# Give the fetched tensor a stable public name, independently of upstream implementation details.
for n in pruned.graph.node:
    for i, output in enumerate(n.output):
        if output == name:
            n.output[i] = "vocals_chunk"
for n in pruned.graph.node:
    for i, value in enumerate(n.input):
        if value == name:
            n.input[i] = "vocals_chunk"
pruned.graph.output[0].name = "vocals_chunk"
for value in pruned.graph.value_info:
    if value.name == name:
        value.name = "vocals_chunk"
onnx.checker.check_model(pruned)
onnx.save(pruned, target)
print(f"vocal model: {target.stat().st_size} bytes, sha256={hashlib.sha256(target.read_bytes()).hexdigest()}")
