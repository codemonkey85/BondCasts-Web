// Reveal-on-scroll progressive enhancement; CSS handles reduced motion.
const elements = document.querySelectorAll(".reveal");

if (!("IntersectionObserver" in window)) {
  elements.forEach((element) => element.classList.add("in"));
} else {
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      entry.target.classList.add("in");
      observer.unobserve(entry.target);
    }
  }, { rootMargin: "0px 0px -8% 0px", threshold: 0.08 });
  elements.forEach((element) => observer.observe(element));
}
