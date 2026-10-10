namespace FastbootEnhance.Core.Tests
{
    // Made with the lz4 and xz command line tools from BootImageTests.KernelBlob(KernelSuMarker):
    //   lz4 -l -9 (legacy), lz4 -9 -BD --content-size (frame, linked blocks), lz4 -9 -BX (frame, block checksums), xz.
    static class CompressedFixtures
    {
        public const string Lz4Legacy =
            "AiFMGJwBAAAfAAEAJEBBUk1kCAD//1FMaW51eCB2ZXJzaW9uIDUuMTAuMTk4LWFuZHJvaWQxMi05LTAwMDAxLWdhYmNkZWYgKGJ1" +
            "aWxkZXJAaG9zdCkgIzEgU01QIFBSRUVNUFQKAEtlcm5lbFNVAHN1c2ZzX2luaXQAAAcOFRwjKjE4P0ZNVFtiaXB3foWMk5qhqK+2" +
            "vcTL0tng5+71AQgPFh0kKzI5QEdOVVxjanF4f4aNlJuiqbC3vsXM09rh6O/2AgkQFx4lLDM6QUhPVl1ka3J5gIeOlZyjqrG4v8bN" +
            "1Nvi6fD3AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dzj6vH4BAsSGSAnLjU8Q0pRWF9mbXR7gomQl56lrLO6wcjP1t3k" +
            "6/L5BQwTGiEoLzY9REtSWWBnbnV8g4qRmJ+mrbS7wsnQ197l7PP6Bg0UGyIpMDc+RUxTWmFob3Z9hIuSmaCnrrW8w8rR2N/m7fT7" +
            "AP///////////////////wEAOhAPBAD////////////////////zUGRhYmNk";

        public const string Lz4Frame =
            "BCJNGGxApCAAAAAAAADsnAEAAB8AAQAkQEFSTWQIAP//UUxpbnV4IHZlcnNpb24gNS4xMC4xOTgtYW5kcm9pZDEyLTktMDAwMDEt" +
            "Z2FiY2RlZiAoYnVpbGRlckBob3N0KSAjMSBTTVAgUFJFRU1QVAoAS2VybmVsU1UAc3VzZnNfaW5pdAAABw4VHCMqMTg/Rk1UW2Jp" +
            "cHd+hYyTmqGor7a9xMvS2eDn7vUBCA8WHSQrMjlAR05VXGNqcXh/ho2Um6KpsLe+xczT2uHo7/YCCRAXHiUsMzpBSE9WXWRrcnmA" +
            "h46VnKOqsbi/xs3U2+Lp8PcDChEYHyYtNDtCSVBXXmVsc3qBiI+WnaSrsrnAx87V3OPq8fgECxIZICcuNTxDSlFYX2ZtdHuCiZCX" +
            "nqWss7rByM/W3eTr8vkFDBMaISgvNj1ES1JZYGdudXyDipGYn6attLvCydDX3uXs8/oGDRQbIikwNz5FTFNaYWhvdn2Ei5KZoKeu" +
            "tbzDytHY3+bt9PsA////////////////////AQA6EA8EAP////////////////////NQZGFiY2QAAAAANggnwA==";

        public const string Lz4FrameChecksums =
            "BCJNGHRAvZwBAAAfAAEAJEBBUk1kCAD//1FMaW51eCB2ZXJzaW9uIDUuMTAuMTk4LWFuZHJvaWQxMi05LTAwMDAxLWdhYmNkZWYg" +
            "KGJ1aWxkZXJAaG9zdCkgIzEgU01QIFBSRUVNUFQKAEtlcm5lbFNVAHN1c2ZzX2luaXQAAAcOFRwjKjE4P0ZNVFtiaXB3foWMk5qh" +
            "qK+2vcTL0tng5+71AQgPFh0kKzI5QEdOVVxjanF4f4aNlJuiqbC3vsXM09rh6O/2AgkQFx4lLDM6QUhPVl1ka3J5gIeOlZyjqrG4" +
            "v8bN1Nvi6fD3AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dzj6vH4BAsSGSAnLjU8Q0pRWF9mbXR7gomQl56lrLO6wcjP" +
            "1t3k6/L5BQwTGiEoLzY9REtSWWBnbnV8g4qRmJ+mrbS7wsnQ197l7PP6Bg0UGyIpMDc+RUxTWmFob3Z9hIuSmaCnrrW8w8rR2N/m" +
            "7fT7AP///////////////////wEAOhAPBAD////////////////////zUGRhYmNkXNX9DQAAAAA2CCfA";

        public const string Xz =
            "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4CCjAXRdAABuSIgADtnQVi9n5VSy1pWKfFCtZGoG5YSKUFD2/kr1DGy3+Tl6ufC4ley7" +
            "VEZLph4TCNqmKsjCFgNI9WUIIOFBzZmQuEOgp6o1G9DPDZZiTiw6Y/y3ewnyANXq732vw9lb6fJGjhNcX083oIFVwQuokhV0w0uZ" +
            "h6s0n//gur9Zry2nsbwDoh4y1QYTubEPYAeGEGFnvDauquo/Sdv+rJg9jEY76wBJFGp39c0kTQ3I4U83Au522sGBgLjPzmOQa8tK" +
            "CeyeTv9dTaxNR2mWP02mRCIpBjTf6GGOSLwF87ivnX+KuJ24b6ejfi0DvoGINNcagIsVBV1BOrGQ9Q7tPyrSfzAdFBYayHaKo8MX" +
            "LDZb3NDyzYPW3LaxggN5XyzBZ2zRVehW0uJuyvTFNEdfAjuMZtkajFa1n9KsPVMnVhO3zMPvMIVo4bn8Q/KDqROx83fyvB9igoh2" +
            "OJgKrmsBk1Ws/tOj+L6YKMDUwFe3tAlowmLnAAD+7eWalCUcpgABkAOkQQAA69HCKrHEZ/sCAAAAAARZWg==";

        // BootImageTests.LinkedBlob() compressed with lz4 -B4 -BD: 64 KiB blocks whose matches reach
        // back into the previous block.
        public const string Lz4FrameLinked =
            "BCJNGERAXnsYAAD///////////////////////////////943ARlqh+tHVra5awbHl8TcHls/RD/Ga9gHQSstB0CK0Z4czry31+u" +
            "twhZ0e45EMtIlbXMiSkR/wa2Yi7fPPk1/UuUKMoJfESzAl6WX7PqbazULYFuaa/g5odMnATn0jZdLGDJ6vR59oag65Mm5GIS1Q3L" +
            "s3cVamo6aLqO23QIRp7zzrMK+NDdaLv4X/ok8tL8GIf7XIe6tDgypZsbPRB893jWf+Jt+BGRKX6TlcsSxVfOWvHUFhjXGbwEW36Z" +
            "ZfGilHHEKqxqqTjEdcetMjgCHwU7LJka/OsV3s9ouuB8vNYelxuaC52+l2PTkvyv36KMlyNFYuvdB2Vw/1iJas/3yu4/HOnkCmjl" +
            "3pONOJx9vddbCdTn4jNEP0qMxKGQ1ri43GFf0Y4ovlkOqlAbUIpqNinmcN9Vd7rcRG1Du6kIF9bA9nsIYXDZLckSclskfsLi2rGy" +
            "BJ4ggHQ3mm+QDN0uXnL1CUi2WNGX6cOMsW7T3RJEYjIMFKevP/oM3tYTzhOGy1egR+RbvtFFtDbViP7SAEHyh7EPg190ZbooRhZS" +
            "34iiE9m/Qu+3EbXeB3/JebrjqFhKqegtqE1QneaYa+Kpms8hTGYqjNWQETeYZ4m7rfNRjROt9RyhAZSssIRs9Yr1KnqR9fOrL4Yy" +
            "uoFFID3DZxSIenWQyGPHB+AewnADmtGLFj8k9sPeK+9dWtHmdhN5ykIWuRCqBdmDMMcKz4XwZsvs76yJTPq3Hxi6wzTftmBKsoAy" +
            "zTmhbt+URBTh86bswfQ5QwbAm2Kd4zrTYe/DU2vQT5Yf7k298wUtl/7E0ZtFJ7SixJTYZD64cbnEH1OLCJUcnl9AIf+XehpNf3CM" +
            "qy98+oAbQsr124z5LLjmfkH2+X8B5Kg2baTsou26cO1UV+ugl2NBiU1NWWjmkrxcqw7zEHkGnaU98VJdLgk7Dc6WbUGe9QospGsW" +
            "Vp2sGgQCKXtmvR2Xg6hWpeXKw0kEUML6c00oFMwxDbxdC1x4j34eihqFgQ/r5qvc0HdBFOkUtYnPXFPYgS4LQxPm/EwVV8UXxIiK" +
            "ffMvyO+379kR1VBGEuyC0M1i0T2hEOjjEa3G9hr7gJFYs7uF1zHo5brgPk6OY3n3a4lUfNDux2o8cACJjFgj7RhFwruM2Bu8hiEU" +
            "o/TM9x4rC+2fyEM850d2QFdlcyv2Nb98QQRCQLbusQwfPb+2n4UD1n2Ap/+0qta9NpzjTgQpOiHvOgcQK2moXJlg02zR8IdF8fC0" +
            "yCfcqa8AKUFGb2nN6Z0jwEF0cB096Vah0gzksHPQEQBPm1UHTowFJcmQb5ILJLkFjOd6KefnFcGhqNqVmPPbJExljgjRsycnkL6z" +
            "nsFa9G6p3gDkk2uYyo/9SVDtM0S3d97+w3JLiN5TUasMQhnLkk+weWZ3TtVVVWSp96hnR1OIXx5RZy4f48qh0vLFODYKOLVdxsxn" +
            "xPqrM3OiAmfZjTc+ZcnqM+TNrQaeaYSPLm4bRiUdyo5eUEnEH2oyC/QAPNVMRDEy0Da02YeJtferVrC5SIKom5rw524kZ3IskEJO" +
            "fEradgPatJdwBWmRRqNZrmg/D6BmcXI9ivqx+aCk7CeJ16Pvf/vfDiWRIlFWEQ/PqoHa6cjabgJuGl//QSiWfVVsttV8KlDRT6PM" +
            "K/7qEsnXh/u7l8178HT7irzmFdcKOYAsYNRgn5hHsn5YFij4VkfIi02tQzK+8xZKZ2diSISMjB3IXpRkGmM2URB2w1osU7yi2eEz" +
            "KiND4rc6nQiBpaYHoEXyvzYR/ahdi/eyzwVR3FiVDJb82bzX6Gte/hkj32rOD2jYrzNrf7oBb+3xl5ugxbsEY0CXtm71NIQ9qbeQ" +
            "LL9emddkOgc0f6q4bVabiH4AgaOTjhSKH/jL5rzJGRDGimpctfDcKT7Evqkplslx8SEgvx19CY9hB2lccxABtq5Ia4lq6dInFqN0" +
            "GhpK2axuQtEy+qYvHaw7Rr9bGCfcXxGZ+Ozo1VszMgbkNwqDk295ytQhoTyMeayb5Wx2Q9pP/CuBNYSbGw2Kq914bn98bN9Ee4oF" +
            "6TQ/cZ6onMUNBvYjW/09Vt3BHcOa39YNhcHci3cBLmvubnajiN/mmz26zZtfQvv2U6Xa9A3BSYFNujeWmzwEawORl1mRYj+Ri0xL" +
            "f3EqaPy1Hb02OlvIX4+99hjoBgWc4PQarfEJoT6vFujlx4x7/7uCPaBbhktCAiSQKZY3KJc98na14KwEPGBwHeabQCyYHS3TTKYY" +
            "y8BgRX7g3aVm9NLgI4mVJF8hWLBimiMfdF6TdfZQVOs/cl96N1b0KbZKVxeaQ0pIqoVNLy4YmP9K69WyHsSe1p/vuRo0oxWcEDKD" +
            "8FL5NvDeAvlG+XkyuqfVmjzEwrqx5dAkfuzed9VtRBDCxMOQ9PIuEkw81SkngrScbGBg4VQGrVr81yBRrMQYteVnu5Is36FSmW5D" +
            "tR7UIpKYabdLmPweEe5ugd35DkUpsbP3cnGc9W+FCNwOeJS1MxpX3jBTvroCqykYUZVCZCZ/IZBqmiLAImmBuGwLugY5SaLuyF9F" +
            "GuaLf/7nVlkNYqUpnbB/aJojnFHuB7E/rFp9xP9Kk4jVc+boS9UWSteXfUI3ffhmHSp18ZcXQRpATw8yKfDHgIViFdwWVKwOW3te" +
            "5HYK3RXf8E7Zy9STRFrRVWaD8dQkv2tu1Xic8Zwwx6CIco0HbHkqf3+hdX+0kZap2CaEkG0eRktIieW77PAzm6VCP0xkgpNeXjM0" +
            "3mN/V2L+KePVUjirA69hZ+T3MXeos/9YhvSU4kXrlkeHSLnM2FOmRXrLp1HughdaQrSLSx4rwBCMFUTPiqHl6FFb2q1kTbLhV9EA" +
            "8mQ3xPavHJhnVYWfnze+LRKH9TZNF5V4slxkaPBFW95Fvkl/cwIm7oOlOLI9XejlYpRhorCu7iyTGhDeqxpi1wFTLWEJFMsmV2a8" +
            "ESHYigV5B11BR+9cjgj1yi1IsN2E4HtegvC7Ateb8YrWhX6cJQ45XypLs9o1yEUKbQDexXyZjlH6YNHDmgecGRegKRfc2IPidvTR" +
            "XbqNYke2C3wRWOPkgOCQLQcHUsHi7Kmx8sOQPFw8eiHgtQ6k+R+hYrmwd9ZjTbqnxrY2t1xv2uwmcu+8RZYVe1i9AmSZwPppthwO" +
            "unFZFBf2PmrW/7ZqtKqAq1sVmv23v2sj+Z6zTmcAM17qIhvYVpI4pXdE2pDfd4Z9ckVhaq4NVye7gQ7VNo6OIL3vrDw6jzuh8KPy" +
            "hUeEHB1YTQKUNjgYyAK56setWMQMjE8keZ3n4RSbkxeCw8nZRGSklrMpO0e8J79eXKVWb9qtu5vIVpLAt8+MYb0sP1eo8MI1/14M" +
            "fLyACoPM9IIlQnqqKF89h3BC5Afob1jdKwJUIPavrqNPgWcScU500X78SZTjdyu+im4ze8PQIZzwCeY18v3v+FfBM1BAHPs9FMB0" +
            "8uZI9jCm8RJgCxheczx3AHpBH/sELTU8OwdsY759RlM8Rwp41FyE2y/Yf+dbqAP4ZvtPqb9oldpM33iDSlFGPOkf9YijRN/lYEE9" +
            "lEvMZShyN8PRIKGZZvrgdjTcKneJcYZB/pT1uohqX4o+PD9U5xUOtEsfcPk2viGfTGmek5BNkyazoAfNHMVKnrskmoqOx5db8LVt" +
            "bKQPuyyl7EZRq/Rf232+FM/66hmw5f50vnMB7O6W2S+sB2XGUxZauWgx3gGaNuayenhQ5fqTwGen8DsjpheET3C4OVlNd62QknuE" +
            "k5q1EXqF8HDGs51iCf5c61S7Sq1lcA0DjVKg3WOF314rEtI3A6e5wdMS3N3yfA+LmWUHTQhjYDp6mmrhyvq34+E8ZU7nmiy7JVEm" +
            "KLzXYZMI3CZb0QMIVjddq7FcqVqLz05GUbsUn9fUplU7/cirerlYgM9YBl3P3tM9RrJLIM8LgU41Gs9sjfdKQA9OCEO5xhHsojQm" +
            "tx+ENPeXY5ZhJq4OVPSagelUqHa/nEZNg8dFP0LL/xluu0S4sp0JQ3UJzyswhtTkcKT8YKrZflDCHE8ZWkNOmNsdNJZETzoKte+H" +
            "EbriYK5Z9g5B2r6OyVkx+9kMAbxbVb1tCIeljqN4Ue74ywDVmPrB9FElRA1wnn1itjH+3xo0EQdEUZifxhamGxg52cxarH3HyGVi" +
            "lfzreZznnzFQjtCUGYR8IAMr52Yoqd5k3azIo5/ecRG7J5acwaYwk6Ztgby8/uA0UolDCirjkZqfRqWrk8siSBqo+V/oKbsbe3Cf" +
            "AQfsU8wmmoMJPP4qcqvgmwy8x07/SDdrOSqpGMFlToPbFISu4RUV/tx0MoHlmTICPjIKzy/ei0Winl8gfvzBhMP5/8pquLEM+uu3" +
            "b+oB9DO6DMx80HRe3RNegalJ2oD6Mc6WfmCmKAXcucTLenrdhfdjRC3ZoWaNA4BIjRuVQz2crX+iungwQfn1k22LnadPbk+sukL+" +
            "XazrHOyj68XBpnys8w5wxoIZybhbLRgBCeyW5zr7BCP5kkJMpcOxBLSLSuQqm3so1+PFGlPjFz+6sedEKMkWp/OXgSarxmbo0kZ9" +
            "bFwg0jXl+WQytGeA73T32n6s+nAjAxQfwjaCAgnVLo5dwXRuhmWyHxm3kkup7RfnrQBoCy/iX5Q6ftF8AXOc9M+QcUayD2Zu55I7" +
            "cgTfaIXoWuvfYkbSWaS7isxmZuYo2gPvU1ObbsezfjrqmQBCLEjzWeNW6MTmGSSF69FkZaGVUcSM/8EhpJa0Z60ACf1V5AOLD6CA" +
            "jrKw85rQ2SZy1xmyyupOOdZ9qGqastCy8XfVwlpc28aGNYJfonQXZGRDfV3XZNq758kB2azyorN3CjNoncGdcQbop590ziPWk1za" +
            "jMXyOf+sALg5dGUOxOWHFi0sN387ZEI37efWlTRYm0h1bI0DvXub5sLKAaudlWaVSd+1Gdv6u0l/1oUENgl331GiLNKtrRGirIbB" +
            "EvbeHsn0jzMCCR/M9o4W0wmUAGm2WUHIPIab2wYugJrbhxknymYSqtN8LO1VU4C2PAaU6ObDH1jaApxwKIKRhp0InPqv10ISI+53" +
            "lYAE7Sk0RB1sh8gW1UPXCEkg34998jHBDpF+9mGMFVo8jNo0IYoG826woP02EL843Qd8VHZvud/ciJUGkqSrLFuUDS2Y1QNvZnuD" +
            "rXTZcIKk9ZO3aa9aTturU0uaA2ZFaa7VsIav9efL/an8hTajQBcCWpFPSMtmgxPvm3RD//3f9GqV4q6StRVRSsRkvhHJrkYLuB/T" +
            "w39N2KLQFfourTR7BLWka5QwvI2e3AcKcHXg2NwG0uj78rESpcF3wayr4F7b5sV9sbA6yt0cZrF5nCXQB1JfHiwWJtguRpIvL1Fc" +
            "sycCPX2ijgQfP1x6TeSW9UjCdL4ODEMkQ3Y3Xd1w9iCSH+ECAyAglKX8dDp0abzKVkZE6E9U0SRqtm3QYyfyohX9WQ+m4cBidEs9" +
            "nr7AZRkTy5rVe70YnWYkv+RYDJ5dUvhDWQCexeSkZo6fxaHpXJaJT3so/XEHT/kFkFbWUMZiR3japg+HlUyDPzUfxKAK+Lg61e/N" +
            "M9s9bRfM8z9iV1wkz4oz7F7rhdwoVmTg4pxRkDPYZ+W1kUe4zZLH/iuIWsUgYD3to15npyH9Lq8IirlJEn8p/VKghhH/12vLA9By" +
            "Y0NUXcqtZgkdAyAT6GlKRxuvv83MX4ASsoaVd11DqLw3EV8rO9N8hHekt6xCXVcUPuOS6gw1qgLSOcODbkGGUXdgeD74cBjw6966" +
            "kHc8JjjphPAZdC2Vb6nwWia0V+VJXAqYL7jZsWSyiCJYPV4xylZs0PK8nrpxboNS+encO7we5raWNWe/+QR755xOHPDju3IqDZvg" +
            "kB9byqGUwygAwfXMywomc7vHGY0A9GDM2ptSb/cBy0uPk6aeQY77k6xxGZXOJEJUHOUpmGX3LMhnCVE5LmcCODDiidYqgEp2uOTC" +
            "F7d8Q2JbbWxzDz5qZxBG8qrI1vr96yc6SlMCZiecMigsf6nf9e90Gvb1YNIBTGprmLzoafQ3ePLStawIAM9yg6sdRM9lURVr+RD3" +
            "He+UjPXeAjMIdIzu6kZGFehYybwKbIrlzAv4ZpZUV5kBh1/FyFGTT5DVY6BYfwHVvocDKvdHvSSMPlyA1eDD3jYjJS0dymfWt3Az" +
            "qHVT3IZC5vTSgBWvmMhW9ntedEtgdhpf3CoidV42m3QceZzPvizLFc3frFfaOKs9bmvo57V4xIXpKC+1f8WwnxScMdIWM5DdMkrm" +
            "rYGUlapyMhSQdfR6Dm1PyyDTHAYsqRbq3iYRjMUb+5qqJ5RttJ7t0D06opJyf9bOrfDz9ZLzH6DrhX0XRJ0ooEZh8InUDNL155o5" +
            "Zio49w5UbwLVSQruKvDMLorS0wrReMpgQ4HSGlmFLnNNrbDpZA7nAf/KBb1f5ZLeO+jT2gQ4+JvyMwj+1hCNBp9z/+Sp0/GnyYz9" +
            "wxnDcUOE/bbOEH+EW+9jyLRDf1CstPU8H7SaJa6NCAo3MIH/F7yKlB/PRAZXkIkodXnKDxvlq4DUXVsazaK1RFN6GNQQOwaEBRh/" +
            "FUYvG0F5TupiDIkM4zl8D2q7YAvCTdB/iudZ6p+4xClK4iKUKvic1+A4+JB8iOqtqzIgg51fC6+3VW+t/XCpOj3hw2n8KjqHIlWK" +
            "sGT3fS516DiV7L8Cn/QQ08i1ZaGphv1qFc9GAPW/HJfjs78PiWGWArjIpo1H2NHfsuHOg2687G0Nu4bGFlFFd6KBGXe2B/XVZElN" +
            "haD8UOrjluUngndnJsvXIWTkboiDZOU7jq7/uwkSh+rAovhFTNTv4WXcUSfrxzxPs1eunRKi914qaeCh9zmZthhDZGhe/AL5FnTg" +
            "4hZ3AUeKZqs8fNPRuiXeQ1/1UZjpZH7XcXbU6GcRRL1R/lY/Q8EJ+x0Zb4mHVQXArV3Tm7t8SOoX7tO4YS5BgCz82Q55MDk290Hv" +
            "OP2U1+5Dhje8ZDibNBQdcoshRWXAK2Z7hiKv7vaETcr2VLlLZ9nI9BEccG9OH1soed0upxYGlrG+dxce0XuUedrEnVnAJ3uCshv9" +
            "u9DvAW2azsEAGXTj9c+uPsNgk25z/YQW+pTl+Fq5mLZ3Uj5lugUt12djL6W/0QD+1TqGMzF2zv4mecuV8zzNc5xrwuGG0zBH1Yru" +
            "NIbbpS5L+Ao7Kl/ZLNQIkKzGU/EMIbQ1zEocEOlBJmnpwgbVpx4y2d7Wztx4/NjulEoYO1n/xU1+ffcFlaTRVxrsasg/RYCpda7b" +
            "EbqXgpum96HlMy01w0+YfUcj9SFyIXsfttC4eTNXKaCuy5yeUThGByVxpJ1QmBLGDmwsrnBV6DzXMXC2L9zAM+2BBMTGpK9hNOtb" +
            "laqvY+mSRkISCw6q2mM6ZM5WXOqtBLbGi7RAqchLeUq1iu4E/wqLLuP5pToQksONW14wICUsQduM0WasVKDzapSorG0gsBPpuvNw" +
            "D/ecpOLzAK7ZtJ36Ol/WbjEdbDKyy7xBFWS7tGouscmhp7i2Wy70MI5yj7rbm3uurzrnDBvxfgZ04mJHqtjsB2k4INDxpvENtnY6" +
            "V7fZeX+exElbnJ+mUQOz36WgUFqC3sEKypzxsZ3J2tarxbGGOnfSyvYubet8Oe5opUUutJN5W7B8U6frOUCzm43+uls2sn6itsjz" +
            "B96OrDcfkAmykLEeSL4OYlc3khzB1VmNsMvG3glxY8ixz3sFgut5egvT/yELaca4rPE4K4Bcz8I8UisKNwH2haCoeBGC4vkZebPa" +
            "fTJUckKkvqn5EPhX8bkhWhxjPXnBjr/853DaB32ziLpK81YKl/X2Xp6pg+mx3pLKKsf95RYxbn+ggq2cP3GqGJ+HSodQWfHOMYLU" +
            "fptj/PRNmhkxGkMMelTxMq/3anFslSYF4QWGCHHL1cMNRv+3T5wTaVZlevFkGvVBODxYNixECGAPTjaSwITYKI9LbBtTMGzGS5GP" +
            "dbYLxrMwp/JQ08NlNAnCH+Yzc8Xc+0JggHkh2wYJNDvx5z+ccBf/////////////////////////////////////////////////" +
            "////////////////////////////////////////////////////////////////////////////////////////////////////" +
            "////////////////////////////////////////////////////////////////////////////////////////////////////" +
            "/////////////////////////////////////////////////////////////2FQVlzqrQRpAAAAD2Dq////////////////////" +
            "////////////////////////////////////////////////////////////////////////////////////////////////////" +
            "///////XUDvx5z+cAAAAAIb27MU=";
    }
}
