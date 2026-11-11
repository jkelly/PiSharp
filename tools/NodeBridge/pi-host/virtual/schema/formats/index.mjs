// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/schema/src/formats/index.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { IsDate } from "./typebox/date.mjs";
import { IsDateTime } from "./typebox/date_time.mjs";
import { IsDuration } from "./typebox/duration.mjs";
import { IsEmail } from "./typebox/email.mjs";
import { IsHostname } from "./typebox/hostname.mjs";
import { IsIdnEmail } from "./typebox/idn_email.mjs";
import { IsIdnHostname } from "./typebox/idn_hostname.mjs";
import { IsIPv4 } from "./typebox/ipv4.mjs";
import { IsIPv6 } from "./typebox/ipv6.mjs";
import { IsIri } from "./typebox/iri.mjs";
import { IsIriReference } from "./typebox/iri_reference.mjs";
import { IsJsonPointer } from "./typebox/json_pointer.mjs";
import { IsJsonPointerUriFragment } from "./typebox/json_pointer_uri_fragment.mjs";
import { IsRegex } from "./typebox/regex.mjs";
import { IsRelativeJsonPointer } from "./typebox/relative_json_pointer.mjs";
import { IsTime } from "./typebox/time.mjs";
import { IsUri } from "./typebox/uri.mjs";
import { IsUriReference } from "./typebox/uri_reference.mjs";
import { IsUriTemplate } from "./typebox/uri_template.mjs";
import { IsUrl } from "./typebox/url.mjs";
import { IsUuid } from "./typebox/uuid.mjs";
export const formats = Object.freeze({
    date: IsDate,
    "date-time": IsDateTime,
    duration: IsDuration,
    email: IsEmail,
    hostname: IsHostname,
    "idn-email": IsIdnEmail,
    "idn-hostname": IsIdnHostname,
    ipv4: IsIPv4,
    ipv6: IsIPv6,
    iri: IsIri,
    "iri-reference": IsIriReference,
    "json-pointer": IsJsonPointer,
    "json-pointer-uri-fragment": IsJsonPointerUriFragment,
    regex: IsRegex,
    "relative-json-pointer": IsRelativeJsonPointer,
    time: IsTime,
    uri: IsUri,
    "uri-reference": IsUriReference,
    "uri-template": IsUriTemplate,
    url: IsUrl,
    uuid: IsUuid
});
