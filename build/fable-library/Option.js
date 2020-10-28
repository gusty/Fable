import { Union } from "./Types.js";
import { compare, equals, structuralHash } from "./Util.js";
// Using a class here for better compatibility with TS files importing Some
export class Some {
    constructor(value) {
        this.value = value;
    }
    toJSON() {
        return this.value;
    }
    toString() {
        return this.ToString();
    }
    // Don't add "Some" for consistency with erased options
    ToString() {
        return String(this.value);
    }
    GetHashCode() {
        return structuralHash(this.value);
    }
    Equals(other) {
        if (other == null) {
            return false;
        }
        else {
            return equals(this.value, other instanceof Some ? other.value : other);
        }
    }
    CompareTo(other) {
        if (other == null) {
            return 1;
        }
        else {
            return compare(this.value, other instanceof Some ? other.value : other);
        }
    }
}
export function some(x) {
    return x == null || x instanceof Some ? new Some(x) : x;
}
export function value(x) {
    if (x == null) {
        throw new Error("Option has no value");
    }
    else {
        return x instanceof Some ? x.value : x;
    }
}
export function ofNullable(x) {
    // This will fail with unit probably, an alternative would be:
    // return x === null ? undefined : (x === undefined ? new Some(x) : x);
    return x == null ? undefined : x;
}
export function toNullable(x) {
    return x == null ? null : value(x);
}
export function flatten(x) {
    return x == null ? undefined : value(x);
}
export function toArray(opt) {
    return (opt == null) ? [] : [value(opt)];
}
export function defaultArg(opt, defaultValue) {
    return (opt != null) ? value(opt) : defaultValue;
}
export function defaultArgWith(opt, defThunk) {
    return (opt != null) ? value(opt) : defThunk();
}
export function filter(predicate, opt) {
    return (opt != null) ? (predicate(value(opt)) ? opt : undefined) : opt;
}
export function map(mapping, opt) {
    return (opt != null) ? some(mapping(value(opt))) : undefined;
}
export function map2(mapping, opt1, opt2) {
    return (opt1 != null && opt2 != null) ? mapping(value(opt1), value(opt2)) : undefined;
}
export function map3(mapping, opt1, opt2, opt3) {
    return (opt1 != null && opt2 != null && opt3 != null) ? mapping(value(opt1), value(opt2), value(opt3)) : undefined;
}
export function bind(binder, opt) {
    return opt != null ? binder(value(opt)) : undefined;
}
export function tryOp(op, arg) {
    try {
        return some(op(arg));
    }
    catch (_a) {
        return undefined;
    }
}
// CHOICE
export class Choice extends Union {
    constructor(tag, ...fields) {
        super();
        this.tag = tag | 0;
        this.fields = fields;
    }
    cases() { return ["Choice1Of2", "Choice2Of2"]; }
}
export class Choice3 extends Union {
    constructor(tag, ...fields) {
        super();
        this.tag = tag | 0;
        this.fields = fields;
    }
    cases() { return ["Choice1Of3", "Choice2Of3", "Choice3Of3"]; }
}
export class Choice4 extends Union {
    constructor(tag, ...fields) {
        super();
        this.tag = tag | 0;
        this.fields = fields;
    }
    cases() { return ["Choice1Of4", "Choice2Of4", "Choice3Of4", "Choice4Of4"]; }
}
export class Choice5 extends Union {
    constructor(tag, ...fields) {
        super();
        this.tag = tag | 0;
        this.fields = fields;
    }
    cases() { return ["Choice1Of5", "Choice2Of5", "Choice3Of5", "Choice4Of5", "Choice5Of5"]; }
}
export class Choice6 extends Union {
    constructor(tag, ...fields) {
        super();
        this.tag = tag | 0;
        this.fields = fields;
    }
    cases() { return ["Choice1Of6", "Choice2Of6", "Choice3Of6", "Choice4Of6", "Choice5Of6", "Choice6Of6"]; }
}
export class Choice7 extends Union {
    constructor(tag, ...fields) {
        super();
        this.tag = tag | 0;
        this.fields = fields;
    }
    cases() { return ["Choice1Of7", "Choice2Of7", "Choice3Of7", "Choice4Of7", "Choice5Of7", "Choice6Of7", "Choice7Of7"]; }
}
export function choice1Of2(x) {
    return new Choice(0, x);
}
export function choice2Of2(x) {
    return new Choice(1, x);
}
export function tryValueIfChoice1Of2(x) {
    return x.tag === 0 ? some(x.fields[0]) : undefined;
}
export function tryValueIfChoice2Of2(x) {
    return x.tag === 1 ? some(x.fields[0]) : undefined;
}
// RESULT
export class Result extends Union {
    constructor(tag, ...fields) {
        super();
        this.tag = tag | 0;
        this.fields = fields;
    }
    cases() { return ["Ok", "Error"]; }
}
export function ok(x) {
    return new Result(0, x);
}
export function error(x) {
    return new Result(1, x);
}
export function mapOk(f, result) {
    return result.tag === 0 ? ok(f(result.fields[0])) : result;
}
export function mapError(f, result) {
    return result.tag === 1 ? error(f(result.fields[0])) : result;
}
export function bindOk(f, result) {
    return result.tag === 0 ? f(result.fields[0]) : result;
}
