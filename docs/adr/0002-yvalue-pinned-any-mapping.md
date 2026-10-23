# Map JS Any values through a YValue struct

JS values have no lossless CLR equivalent: the integer versus float choice decides which bytes are written. A public `YValue` discriminated struct keeps round trips lossless and makes byte-exact interop provable. `object?` was rejected because it hides that distinction.
