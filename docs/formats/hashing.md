# 哈希格式规范

> 实现：`src/NexusP2P.Core/Hashing/`
> 回归护栏：`tests/NexusP2P.Core.Tests/Hashing/KnownVectorsTests.cs`
>
> **网页端（Task 5.2）必须逐字节复现本规范**，否则浏览器算出的分片根与
> exe 算出的不一致，接收方会把每个分片都判为校验失败而无限重传 ——
> 表现为「速度为零但不报错」。这是本规范存在的唯一理由。

## 算法与参数

| 项 | 值 | 理由 |
|---|---|---|
| 哈希算法 | SHA-256 | 浏览器 `crypto.subtle.digest('SHA-256')` 原生支持，无需 WASM（AD-2） |
| 叶子块 | 64 KiB | 只借用 BEP-52 的树结构、不与 BitTorrent 互操作，所以放大到 4 倍以减少哈希次数 |
| 分片 | 1 MiB（16 个叶子） | 续传粒度与协议开销的折中，可调 |

参数写进传输清单，两端按清单里的值计算，不硬编码。

## 域分隔前缀

四类哈希各带一个不同的前缀字节：

| 前缀 | 用途 |
|---|---|
| `0x00` | 叶子 |
| `0x01` | 内部节点 |
| `0x02` | 分片根 |
| `0x03` | 文件根 |

前缀不是装饰，有两个实际作用：

1. **抵御第二原像攻击**（同 RFC 6962 / Certificate Transparency 的做法）：
   攻击者无法把一棵子树的根冒充成一个叶子。
2. **让「奇数节点直接上提」变得安全**。若无域分隔，一个被上提的叶子哈希
   可能与某个内部节点哈希相等，造成树形歧义；有了前缀，两者哈希输入的
   首字节不同，要相等就得先攻破 SHA-256。

## 计算规则

```
LeafHash(data)        = SHA256( 0x00 ‖ data )
NodeHash(left, right) = SHA256( 0x01 ‖ left ‖ right )      // 各 32 字节
```

### 折叠成根

```
ComputeRoot(hashes):
    要求 hashes 非空
    while len(hashes) > 1:
        next = []
        for i in 0, 2, 4, ... < len(hashes):
            if i+1 < len(hashes): next.append(NodeHash(hashes[i], hashes[i+1]))
            else:                 next.append(hashes[i])       # 上提，不复制、不补位
        hashes = next
    return hashes[0]
```

**奇数节点时最后一个直接上提**，不复制自身、不用补位哈希。
（复制自身会引入 CVE-2012-2459 那类歧义。）

### 分片根与文件根

```
PieceRoot = SHA256( 0x02 ‖ pieceLength_be32  ‖ ComputeRoot(该分片的叶子哈希) )
FileRoot  = SHA256( 0x03 ‖ fileLength_be64   ‖ ComputeRoot(全部分片根)      )
```

长度用**大端**编码：分片长度 4 字节，文件长度 8 字节。

长度绑定让根本身自描述，不依赖清单里的长度字段消除歧义 ——
「空内容」与「一个零字节」因此必然不同根。

### 切分规则

- 叶子数 = `max(1, ceil(length / LeafSize))`
- 分片数 = `max(1, ceil(length / PieceSize))`
- **末尾的叶子与分片允许不足**，按实际长度哈希，**不做零填充**
- **空内容产出恰好一个空分片，含恰好一个空叶子**
  （这样「分片数为 0」这种需要处处特殊处理的状态就不存在）

### 每个文件独立成树

分片**不跨文件边界**。文件夹里每个文件各自有分片列表与文件根。
这与 BitTorrent v1 的「整个种子是一条字节流」不同，理由是接收端要能
独立写入、独立续传每个文件。

## 固定向量

以下值由本实现固化，任何改动语义的重构都会让 `KnownVectorsTests` 失败。

```
LeafHash("")                      = 6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d
LeafHash("hello")                 = 8a2a5c9b768827de5a9552c38a044c66959c68f6d2f21b5260af54d2f87db827
NodeHash(0x00…00, 0x00…00)        = ae0798d0ecaed2b778eddebf18f071a561c53658c05e76cedecc27cafbdbc577
```

`LeafHash("")` 恰好等于单字节 `0x00` 的 SHA-256（公认值），
这可以作为「前缀确实被应用了」的独立交叉验证。

默认参数（64 KiB 叶子 / 1 MiB 分片）下的文件根：

| 内容 | 长度 | 分片数 | 文件根 |
|---|---|---|---|
| 空 | 0 | 1 | `cf47a4b1ae0e5cf4bfc325eb995203718a18692fda59074b3cbd9809d5f98227` |
| `"hello"` | 5 | 1 | `f7781da690db514af7720196438ff907268bbaa5a65926b0a936445d3ca46bcd` |
| 单个 `0x00` | 1 | 1 | `ecfcc24c6b6129fc16dd6c562b73f8591eb23fa0a7394d57baa210371d46b3fd` |
| 64 KiB 全零 | 65536 | 1 | `7cbb44ef39d389f45f0634ec4294d8900e9863eb347f3f601395731156cbd122` |
| 1 MiB 全零 | 1048576 | 1 | `c7c614582b09d7b416aa0630250e130d60fba43e8852554721550e6a6eada6c4` |
| 1 MiB + 1 全零 | 1048577 | 2 | `2964a16460364dd7391025f5ad80db9e1c3de545d4c353ee753d667e1703fef8` |

## 兼容性警告

改变本规范的任何一条（前缀、折叠顺序、长度绑定、字节序、切分规则）
都会让**已有的 `.part` 文件全部校验失败**，续传静默失效。
若确实要改，必须是有意识的决定，并同步更新固定向量。
