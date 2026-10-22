(function (root) {
    "use strict";
    // Visual hints only. Docker Compose remains the authoritative parser/validator.
    function tokens(text, language, maximumTokens = Infinity) {
        const result = [];
        let plain = "";
        function flush() { if (plain) { result.push({ kind: "plain", text: plain }); plain = ""; } }
        for (let i = 0; i < text.length;) {
            if (result.length >= maximumTokens) { plain += text.slice(i); break; }
            const start = i;
            let kind = "plain";
            if (text[i] === "#") {
                kind = "comment";
                while (i < text.length && text[i] !== "\r" && text[i] !== "\n") i++;
            } else if (text[i] === '"' || text[i] === "'") {
                kind = "string";
                const quote = text[i++];
                while (i < text.length) {
                    const c = text[i++];
                    if (c === "\\" && quote === '"' && i < text.length) i++;
                    else if (c === quote) break;
                }
            } else if (text[i] === "$" && text[i + 1] === "{") {
                kind = "variable"; i += 2;
                while (i < text.length && text[i] !== "}" && text[i] !== "\r" && text[i] !== "\n") i++;
                if (text[i] === "}") i++;
            } else if (/[A-Za-z_]/.test(text[i])) {
                i++;
                while (i < text.length && /[A-Za-z0-9_.-]/.test(text[i])) i++;
                const word = text.slice(start, i);
                if (text[i] === ":" || (language === "env" && text[i] === "=")) kind = "key";
                else if (/^(FROM|RUN|COPY|ADD|ARG|ENV|WORKDIR|ENTRYPOINT|CMD|EXPOSE|USER|VOLUME|LABEL|HEALTHCHECK|ONBUILD|SHELL|STOPSIGNAL|true|false|null)$/.test(word)) kind = "keyword";
            } else { i++; }
            const value = text.slice(start, i);
            if (kind === "plain") plain += value;
            else { flush(); result.push({ kind: kind, text: value }); }
        }
        flush();
        return result;
    }

    function render(pre, text, language, document) {
        const fragment = document.createDocumentFragment();
        // Limit DOM growth to O(sqrt(characters)); the remaining suffix is still shown verbatim without coloring.
        for (const token of tokens(text, language, Math.ceil(Math.sqrt(text.length)))) {
            const node = document.createElement("span");
            node.className = "syntax-" + token.kind;
            node.textContent = token.text;
            fragment.appendChild(node);
        }
        pre.replaceChildren(fragment);
    }

    function initialize(document) {
        document.querySelectorAll("[data-language]").forEach(function (element) {
            if (element.dataset.highlighted) return;
            element.dataset.highlighted = "true";
            const language = element.dataset.language;
            if (element.tagName !== "TEXTAREA") { render(element, element.textContent, language, document); return; }
            // The original form control is never replaced or assigned to: selection, submitted text and browser line endings stay intact.
            const wrapper = document.createElement("div"); wrapper.className = "syntax-editor";
            const preview = document.createElement("pre"); preview.className = "syntax-preview";
            preview.setAttribute("aria-hidden", "true");
            element.parentNode.insertBefore(wrapper, element);
            wrapper.appendChild(preview); wrapper.appendChild(element);
            element.classList.add("syntax-input");
            element.setAttribute("wrap", "off");
            function update() { render(preview, element.value + "\n", language, document); }
            function scroll() { preview.scrollTop = element.scrollTop; preview.scrollLeft = element.scrollLeft; }
            element.addEventListener("input", update); element.addEventListener("scroll", scroll);
            update(); scroll();
        });
    }

    if (typeof module !== "undefined" && module.exports) module.exports = { tokens, render, initialize };
    if (root.document) {
        if (root.document.readyState === "loading") root.document.addEventListener("DOMContentLoaded", function () { initialize(root.document); });
        else initialize(root.document);
    }
})(typeof window !== "undefined" ? window : globalThis);
