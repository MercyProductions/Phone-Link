// Project-owned native fixture, not an extracted Apple or user-device binary.
__attribute__((visibility("default"))) int phone_link_probe(int input) {
    return input + 42;
}
