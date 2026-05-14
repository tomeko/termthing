
#r "C:/Users/tomek/.nuget/packages/avalonia/11.0.0/lib/net6.0/Avalonia.Base.dll"
open Avalonia.Input
printfn "%A" (typeof<DataObject>.GetMethods() |> Array.map (fun m -> m.Name))
