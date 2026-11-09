import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper
from tokenizers import Tokenizer
from tokenizers.models import WordLevel
from tokenizers.pre_tokenizers import Whitespace

GENRE_CLASSES = ("Electronic", "Experimental", "Folk", "Hip-Hop", "Instrumental", "International", "Pop", "Rock")


def finished(graph):
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 9
    onnx.checker.check_model(model)
    return model.SerializeToString()


def spectrogram_model(width=6, seed=0):
    rng = np.random.default_rng(seed)
    weights = [numpy_helper.from_array(rng.standard_normal((128, width)).astype(np.float32), "first"),
               numpy_helper.from_array(rng.standard_normal((width, len(GENRE_CLASSES))).astype(np.float32), "second")]
    nodes = [
        helper.make_node("ReduceMean", ["mel"], ["pooled"], axes=[1, 3], keepdims=0),
        helper.make_node("MatMul", ["pooled", "first"], ["embedding"]),
        helper.make_node("MatMul", ["embedding", "second"], ["logits"]),
        helper.make_node("Softmax", ["logits"], ["probabilities"], axis=1),
    ]
    inputs = [helper.make_tensor_value_info("mel", TensorProto.FLOAT, ["batch", 1, 128, "frames"])]
    outputs = [helper.make_tensor_value_info("probabilities", TensorProto.FLOAT, ["batch", len(GENRE_CLASSES)]),
               helper.make_tensor_value_info("embedding", TensorProto.FLOAT, ["batch", width])]
    return finished(helper.make_graph(nodes, "spectrogram", inputs, outputs, weights))


def word_tokenizer():
    tokenizer = Tokenizer(WordLevel({"[UNK]": 0, "[PAD]": 1, "bad": 2, "good": 3}, unk_token="[UNK]"))
    tokenizer.pre_tokenizer = Whitespace()
    return tokenizer.to_str()


def text_model(bad_id=2):
    constants = [numpy_helper.from_array(np.array(bad_id, dtype=np.int64), "bad_id"),
                 numpy_helper.from_array(np.array([1], dtype=np.int64), "axes"),
                 numpy_helper.from_array(np.array(4.0, dtype=np.float32), "four"),
                 numpy_helper.from_array(np.array(1.0, dtype=np.float32), "one"),
                 numpy_helper.from_array(np.array(0.0, dtype=np.float32), "zero")]
    nodes = [
        helper.make_node("Equal", ["input_ids", "bad_id"], ["is_bad"]),
        helper.make_node("Cast", ["is_bad"], ["bad"], to=TensorProto.FLOAT),
        helper.make_node("ReduceSum", ["bad", "axes"], ["count"], keepdims=1),
        helper.make_node("Mul", ["count", "four"], ["scaled"]),
        helper.make_node("Sub", ["scaled", "one"], ["toxic"]),
        helper.make_node("Mul", ["count", "zero"], ["fine"]),
        helper.make_node("Concat", ["fine", "toxic"], ["logits"], axis=1),
    ]
    inputs = [helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["batch", "tokens"]),
              helper.make_tensor_value_info("attention_mask", TensorProto.INT64, ["batch", "tokens"])]
    outputs = [helper.make_tensor_value_info("logits", TensorProto.FLOAT, ["batch", 2])]
    return finished(helper.make_graph(nodes, "text", inputs, outputs, constants))
