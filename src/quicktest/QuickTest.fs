module QuickTest

type Point =
    { x: int; y: int }
    static member Zero = { x=0; y=0 }
    static member Neg(p: Point) = { x = -p.x; y = -p.y }
    static member (+) (p1, p2) = { x= p1.x + p2.x; y = p1.y + p2.y }

let p1 = {x=1; y=10}
let p2 = {x=2; y=20}
[p1; p2] |> List.sum |> (=) {x=3;y=30} |> printfn "%A"
